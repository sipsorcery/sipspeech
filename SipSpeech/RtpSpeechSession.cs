//-----------------------------------------------------------------------------
// Filename: RtpSpeechSession.cs
//
// Description: Example of an RTP session that uses Azure's text-to-speech
// and speech-to-text service to generate an audio stream.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 09 May 2020 Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace sipspeech
{
    public class RtpSpeechSession : RTPSession, IMediaSession
    {
        private const int AUDIO_SAMPLE_PERIOD_MILLISECONDS = 20;
        private const int G722_BITS_PER_SAMPLE = 8;
        private const string CONFIG_SUBSCRIPTION_KEY = "SubscriptionKey";
        private const string CONFIG_REGION_KEY = "Region";

        private static readonly int AUDIO_RTP_CLOCK_RATE = SDPMediaFormatInfo.GetRtpClockRate(SDPMediaFormatsEnum.G722);
        private static readonly int AUDIO_CLOCK_RATE = SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.G722);
        private static readonly int PCM_BUFFER_LENGTH = AUDIO_CLOCK_RATE * AUDIO_SAMPLE_PERIOD_MILLISECONDS / 1000;

        /// <summary>
        /// Values used for the flag used to track the state of the speech synthesizer and the RTP buffer.
        /// </summary>
        private const long TTS_IDLE = 0;
        private const long TTS_BUSY = 1;
        private const long TTS_RESULT_READY = 2;

        private static short[] _silencePcmBuffer = new short[PCM_BUFFER_LENGTH];    // Zero buffer representing PCM silence.

        private uint _rtpAudioTimestampPeriod = 0;
        private SDPMediaFormat _sendingAudioFormat = null;
        private bool _isStarted = false;
        private bool _isClosed = false;
        private uint _rtpEventSsrc;
        private Timer _audioStreamTimer;

        private G722Codec _g722Codec;
        private G722CodecState _g722CodecState;
        private G722Codec _g722Decoder;
        private G722CodecState _g722DecoderState;

        private SpeechSynthesizer _speechSynthesizer;
        private TextToSpeechStream _ttsOutStream;

        private SpeechRecognizer _speechRecognizer;
        private SpeechToTextStream _sttInStream;

        /// <summary>
        /// This buffer is used by the RTP sending thread. When it's informed that a text-to-speech job is complete
        /// it will copy the contents from the speech synthesizer output stream into this buffer. This buffer then
        /// gets used as the audio for RTP sends.
        /// </summary>
        private short[] _ttsPcmBuffer;

        /// <summary>
        /// The current position in the RTP buffer. The RTP thread will continue to send sampled from the PCM buffer until
        /// this position goes past the length of the buffer.
        /// </summary>
        private int _ttsPcmBufferPosn = 0;

        /// <summary>
        /// Crude mechanism being used to co-ordinate the threads reading and writing to the text-to-speech buffer.
        /// A value of 0 means the buffer is ready for a new operation.
        /// A value of 1 means an existing operation is in progress.
        /// A value of 2 means a text-to-speech operation has completed and a buffer is waiting to be copied by the RTP thread.
        /// </summary>
        private long _ttsBusyFlag = 0;

        private readonly ILogger _logger;
        private readonly IConfiguration _config;

        public Action<int> OnDtmfTone;

        /// <summary>
        /// Creates a new basic RTP session that captures and generates and processes audio
        /// stream using Azure services.
        /// </summary>
        public RtpSpeechSession(ILoggerFactory loggerFactory, IConfiguration config)
            : base(false, false, false)
        {
            _logger = loggerFactory.CreateLogger<RtpSpeechSession>();
            _config = config;

            // G722 is the best codec match for the 16k PCM format the speech services use.
            var g722 = new SDPMediaFormat(SDPMediaFormatsEnum.G722);

            // RTP event support.
            SDPMediaFormat rtpEventFormat = new SDPMediaFormat(DTMF_EVENT_PAYLOAD_ID);
            rtpEventFormat.SetFormatAttribute($"{SDP.TELEPHONE_EVENT_ATTRIBUTE}/{AUDIO_RTP_CLOCK_RATE}");
            rtpEventFormat.SetFormatParameterAttribute("0-16");

            var audioCapabilities = new List<SDPMediaFormat> { g722, rtpEventFormat };

            MediaStreamTrack audioTrack = new MediaStreamTrack(null, SDPMediaTypesEnum.audio, false, audioCapabilities);
            addTrack(audioTrack);

            _g722Codec = new G722Codec();
            _g722CodecState = new G722CodecState(AUDIO_RTP_CLOCK_RATE * G722_BITS_PER_SAMPLE, G722Flags.None);
            _g722Decoder = new G722Codec();
            _g722DecoderState = new G722CodecState(AUDIO_RTP_CLOCK_RATE * G722_BITS_PER_SAMPLE, G722Flags.None);

            // Where the magic (for processing received media) happens.
            base.OnRtpPacketReceived += RtpPacketReceived;
            base.OnRtpEvent += OnRtpDtmfEvent;
            this.OnDtmfTone += DtmfToneHandler;

            InitialiseSpeech();
        }

        /// <summary>
        /// Initialises the speech objects required to send requests to Azure.
        /// </summary>
        private void InitialiseSpeech()
        {
            var speechConfig = SpeechConfig.FromSubscription(_config[CONFIG_SUBSCRIPTION_KEY], _config[CONFIG_REGION_KEY]);

            // Create a speech synthesizer that outputs to the backing stream.
            _ttsOutStream = new TextToSpeechStream(_logger);
            AudioConfig audioTtsConfig = AudioConfig.FromStreamOutput(_ttsOutStream);
            _speechSynthesizer = new SpeechSynthesizer(speechConfig, audioTtsConfig);

            // Create a speech recognizer that takes input from the backing stream.
            _sttInStream = new SpeechToTextStream(_logger);
            AudioConfig audioSttConfig = AudioConfig.FromStreamInput(_sttInStream);
            _speechRecognizer = new SpeechRecognizer(speechConfig, audioSttConfig);
            _speechRecognizer.SpeechStartDetected += (sender, e) => _logger.LogDebug($"Speech start detected {e.SessionId}.");
            _speechRecognizer.SpeechEndDetected += (sender, e) => _logger.LogDebug($"Speech end detected {e.SessionId}.");
            _speechRecognizer.SessionStarted += (sender, e) => _logger.LogDebug($"Speech recognizer session started {e.SessionId}.");
            _speechRecognizer.SessionStopped += (sender, e) => _logger.LogDebug($"Speech recognizer session stopped {e.SessionId}.");
            _speechRecognizer.Canceled += (sender, e) => _logger.LogDebug($"Speech recognizer cancelled {e.Reason} {e.SessionId}.");
            _speechRecognizer.Recognizing += (sender, e) => _logger.LogDebug($"Speech recognizer recognizing result={e.Result} {e.SessionId}.");
            _speechRecognizer.Recognized += (sender, e) =>
            {
                _logger.LogDebug($"Speech recognizer recognized result={e.Result.Text} {e.SessionId}.");
            };
        }

        /// <summary>
        /// Events handler for an RTP event being received from the remote call party.
        /// The only RTP events we offer to accept are DTMF tones.
        /// </summary>
        /// <param name="rtpEvent">The RTP event that was received.</param>
        /// <param name="rtpHeader">The header on the RTP packet that the event was received in.</param>
        private void OnRtpDtmfEvent(RTPEvent rtpEvent, RTPHeader rtpHeader)
        {
            if (_rtpEventSsrc == 0)
            {
                if (rtpEvent.EndOfEvent && rtpHeader.MarkerBit == 1)
                {
                    // Full event is contained in a single RTP packet.
                    _logger.LogDebug($"RTP event {rtpEvent.EventID}.");

                    OnDtmfTone?.Invoke((int)rtpEvent.EventID);
                }
                else if (!rtpEvent.EndOfEvent)
                {
                    _logger.LogDebug($"RTP event {rtpEvent.EventID}.");
                    _rtpEventSsrc = rtpHeader.SyncSource;

                    OnDtmfTone?.Invoke((int)rtpEvent.EventID);
                }
            }

            if (_rtpEventSsrc != 0 && rtpEvent.EndOfEvent)
            {
                //_logger.LogDebug($"RTP end of event {rtpEvent.EventID}.");
                _rtpEventSsrc = 0;
            }
        }

        /// <summary>
        /// Test actions to check text to speech integration.
        /// </summary>
        /// <param name="tone">The tone that was pressed.</param>
        private void DtmfToneHandler(int tone)
        {
            if (Interlocked.Read(ref _ttsBusyFlag) == TTS_IDLE)
            {
                Interlocked.Exchange(ref _ttsBusyFlag, TTS_BUSY);

                Task.Run(async () =>
                {
                    bool result = false;

                    switch (tone)
                    {
                        case 0:
                            result = await DoTextToSpeech("Hello and welcome to the SIP speech prototype.");
                            break;
                        case 1:
                            result = await DoTextToSpeech("Peter Piper picked a peck of pickled peppers. A peck of pickled peppers Peter Piper picked.");
                            break;
                        case 2:
                            result = await DoTextToSpeech($"The time is {DateTime.Now.Hour}, {DateTime.Now.Minute}, {DateTime.Now.Second}.");
                            break;
                        default:
                            result = await DoTextToSpeech("Sorry your selection was not a valid option.");
                            break;
                    }

                    _logger.LogDebug($"DoTextToSpeech completed result {result}.");

                    if (result)
                    {
                        // Indicates a pending buffer is ready for the RTP thread.
                        Interlocked.Exchange(ref _ttsBusyFlag, TTS_RESULT_READY);
                    }
                    else
                    {
                        // Something went wrong.
                        _ttsOutStream.Clear();
                        Interlocked.Exchange(ref _ttsBusyFlag, TTS_IDLE);
                    }
                });
            }
            else
            {
                _logger.LogWarning("A pending text to speech task is currently in progress.");
            }
        }

        /// <summary>
        /// Does the work of sending text to Azure for speech synthesis and waits for the result.
        /// </summary>
        /// <param name="text">The text to get synthesized.</param>
        private async Task<bool> DoTextToSpeech(string text)
        {
            using (var result = await _speechSynthesizer.SpeakTextAsync(text))
            {
                if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                {
                    _logger.LogDebug($"Speech synthesized to speaker for text [{text}]");
                    return true;
                }
                else if (result.Reason == ResultReason.Canceled)
                {
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                    _logger.LogWarning($"Speech synthesizer failed was cancelled, reason={cancellation.Reason}");

                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        _logger.LogWarning($"Speech synthesizer cancelled: ErrorCode={cancellation.ErrorCode}");
                        _logger.LogWarning($"Speech synthesizer cancelled: ErrorDetails=[{cancellation.ErrorDetails}]");
                    }

                    return false;
                }
                else
                {
                    _logger.LogWarning($"Speech synthesizer failed with result {result.Reason} for text [{text}].");
                    return false;
                }
            }
        }

        /// <summary>
        /// Starts the media capturing/source devices.
        /// </summary>
        public override async Task Start()
        {
            if (!_isStarted)
            {
                _sendingAudioFormat = base.GetSendingFormat(SDPMediaTypesEnum.audio);

                _isStarted = true;

                await base.Start();

                if (_rtpAudioTimestampPeriod == 0)
                {
                    _rtpAudioTimestampPeriod = (uint)(AUDIO_RTP_CLOCK_RATE / AUDIO_SAMPLE_PERIOD_MILLISECONDS);
                }

                _audioStreamTimer = new Timer(SendRTPAudio, null, 0, AUDIO_SAMPLE_PERIOD_MILLISECONDS);

                await _speechRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                _logger.LogDebug("Speech recognizer started.");
            }
        }

        /// <summary>
        /// Closes the session.
        /// </summary>
        /// <param name="reason">Reason for the closure.</param>
        public override void Close(string reason)
        {
            if (!_isClosed)
            {
                _isClosed = true;

                base.OnRtpPacketReceived -= RtpPacketReceived;

                _sttInStream.Close();
                _speechSynthesizer?.Dispose();
                // During testing it takes a while for the speech recognizer to stop.
                // Provided it doesn't throw (which it shouldn't) it's safe not to await.
                _speechRecognizer.StopContinuousRecognitionAsync();

                base.Close(reason);

                _audioStreamTimer?.Dispose();
            }
        }

        /// <summary>
        /// Timer callback to send the RTP audio packets.
        /// </summary>
        private void SendRTPAudio(object state)
        {
            if (Interlocked.Read(ref _ttsBusyFlag) == TTS_RESULT_READY)
            {
                // This lock is to stop the timer callback from trying to do multiple
                // simultaneous copies.
                lock (this)
                {
                    if (!_ttsOutStream.IsEmpty())
                    {
                        _logger.LogDebug("Copying text-to-speech PCM buffer into RTP send PCM buffer.");

                        _ttsPcmBuffer = _ttsOutStream.GetPcmBuffer();
                        _ttsPcmBufferPosn = 0;
                        _ttsOutStream.Clear();
                        Interlocked.Exchange(ref _ttsBusyFlag, TTS_IDLE);
                    }
                }
            }

            if (_ttsPcmBuffer != null && _ttsPcmBufferPosn + PCM_BUFFER_LENGTH < _ttsPcmBuffer.Length)
            {
                // There are text to speech samples to send.
                byte[] encoded = new byte[PCM_BUFFER_LENGTH / 2];

                _g722Codec.Encode(_g722CodecState, encoded, _ttsPcmBuffer.Skip(_ttsPcmBufferPosn).ToArray(), PCM_BUFFER_LENGTH);

                base.SendAudioFrame((uint)encoded.Length, Convert.ToInt32(_sendingAudioFormat.FormatID), encoded);

                _ttsPcmBufferPosn += PCM_BUFFER_LENGTH;
            }
            else
            {
                short[] pcmInput = _silencePcmBuffer;
                byte[] encoded = new byte[pcmInput.Length / 2];

                _g722Codec.Encode(_g722CodecState, encoded, pcmInput, pcmInput.Length);

                base.SendAudioFrame((uint)encoded.Length, Convert.ToInt32(_sendingAudioFormat.FormatID), encoded);
            }
        }

        /// <summary>
        /// Event handler for receiving RTP packets from a remote party.
        /// </summary>
        /// <param name="mediaType">The media type of the packets.</param>
        /// <param name="rtpPacket">The RTP packet with the media sample.</param>
        private void RtpPacketReceived(SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                var sample = rtpPacket.Payload;
                short[] pcm16kSample = new short[sample.Length * 2];

                _g722Decoder.Decode(_g722DecoderState, pcm16kSample, sample, sample.Length);

                byte[] pcmBuffer = new byte[pcm16kSample.Length * 2];

                for (int i = 0; i < pcm16kSample.Length; i++)
                {
                    // Little endian.
                    pcmBuffer[i * 2] = (byte)(pcm16kSample[i] & 0xff);
                    pcmBuffer[i * 2 + 1] = (byte)((pcm16kSample[i] >> 8) & 0xff);
                }

                _sttInStream.WriteSample(pcmBuffer);
            }
        }
    }
}
