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
using System.IO;
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
    class TextToSpeechAudioOutStream : PushAudioOutputStreamCallback
    {
        public MemoryStream _ms = new MemoryStream();
        private int _posn = 0;

        public override uint Write(byte[] dataBuffer)
        {
            Console.WriteLine($"Bytes written to output stream {dataBuffer.Length}.");

            _ms.Write(dataBuffer, 0, dataBuffer.Length);
            _posn = _posn + dataBuffer.Length;

            return (uint)dataBuffer.Length;
        }

        public override void Close()
        {
            _ms.Close();
            base.Close();
        }

        public void Clear()
        {
            _ms.SetLength(0);
            _posn = 0;
        }

        /// <summary>
        /// Get the current contents of the memory stream as a buffer of PCM samples.
        /// </summary>
        public short[] GetPcmBuffer()
        {
            byte[] buffer = _ms.GetBuffer();
            short[] pcmBuffer = new short[_posn / 2];

            for (int i = 0; i < pcmBuffer.Length; i++)
            {
                pcmBuffer[i] = BitConverter.ToInt16(buffer, i * 2);
            }

            return pcmBuffer;
        }
    }

    public class RtpSpeechSession : RTPSession, IMediaSession
    {
        private const int AUDIO_SAMPLE_PERIOD_MILLISECONDS = 20;
        private const int G722_BITS_PER_SAMPLE = 8;
        private const string CONFIG_SUBSCRIPTION_KEY = "SubscriptionKey";
        private const string CONFIG_REGION_KEY = "Region";

        private static readonly int AUDIO_RTP_CLOCK_RATE = SDPMediaFormatInfo.GetRtpClockRate(SDPMediaFormatsEnum.G722);
        private static readonly int AUDIO_CLOCK_RATE = SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.G722);
        private static readonly int PCM_BUFFER_LENGTH = AUDIO_CLOCK_RATE * AUDIO_SAMPLE_PERIOD_MILLISECONDS / 1000;

        private static short[] _silencePcmBuffer = new short[PCM_BUFFER_LENGTH];    // Zero buffer representing PCM silence.

        private uint _rtpAudioTimestampPeriod = 0;
        private SDPMediaFormat _sendingAudioFormat = null;
        private bool _isStarted = false;
        private bool _isClosed = false;
        private uint _rtpEventSsrc;
        private Timer _audioStreamTimer;

        private G722Codec _g722Codec;
        private G722CodecState _g722CodecState;

        private SpeechSynthesizer _speechSynthesizer;
        private TextToSpeechAudioOutStream _ttsOutStream;
        private bool _ttsReady = false;
        private short[] _ttsPcmBuffer;
        private int _ttsPcmBufferPosn = 0;

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

            // Where the magic (for processing received media) happens.
            base.OnRtpPacketReceived += RtpPacketReceived;
            base.OnRtpEvent += OnRtpDtmfEvent;
            this.OnDtmfTone += DtmfToneHandler;

            InitialiseTextToSpeech();
        }

        /// <summary>
        /// Initialises the text to speech objects required to send requests to Azure.
        /// </summary>
        private void InitialiseTextToSpeech()
        {
            var speechConfig = SpeechConfig.FromSubscription(_config[CONFIG_SUBSCRIPTION_KEY], _config[CONFIG_REGION_KEY]);

            _ttsOutStream = new TextToSpeechAudioOutStream();
            AudioConfig audioConfig = AudioConfig.FromStreamOutput(_ttsOutStream);

            // Creates a speech synthesizer and outputs to the backing stream.
            _speechSynthesizer = new SpeechSynthesizer(speechConfig, audioConfig);
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
            Task.Run(async () =>
            {
                switch (tone)
                {
                    case 0:
                        await DoTextToSpeech("Hello and welcome to the SIP speech prototype.");
                        break;
                    case 1:
                        await DoTextToSpeech("Peter Piper picked a peck of pickled peppers. A peck of pickled peppers Peter Piper picked.");
                        break;
                    default:
                        await DoTextToSpeech("Sorry your selection was not a valid option.");
                        break;
                }

                _logger.LogDebug($"DoTextToSpeech completed.");
            });
        }

        /// <summary>
        /// Does the work of sending text to Azure for speech synthesis and waits for the result.
        /// </summary>
        /// <param name="text">The text to get synthesized.</param>
        private async Task DoTextToSpeech(string text)
        {
            //if (Monitor.TryEnter(_ttsOutStream))
            //{
            //    try
            //    {
                    using (var result = await _speechSynthesizer.SpeakTextAsync(text))
                    {
                        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                        {
                            _logger.LogDebug($"Speech synthesized to speaker for text [{text}]");
                            _ttsReady = true;
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
                        }
                        else
                        {
                            _logger.LogWarning($"Speech synthesizer failed with result {result.Reason} for text [{text}].");
                        }
                    }
            //    }
            //    finally
            //    {
            //        Monitor.Exit(_ttsOutStream);
            //    }
            //}
            //else
            //{
            //    _logger.LogWarning("A speech synthesizer task is already in progress.");
            //}
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

                _speechSynthesizer?.Dispose();

                base.OnRtpPacketReceived -= RtpPacketReceived;

                base.Close(reason);

                _audioStreamTimer?.Dispose();
            }
        }

        /// <summary>
        /// Timer callback to send the RTP audio packets.
        /// </summary>
        private void SendRTPAudio(object state)
        {
            if (_ttsReady)
            {
                lock (this)
                {
                    if (_ttsReady)
                    {
                        _logger.LogDebug("Copying text-to-speech PCM buffer into RTP send PCM buffer.");

                        _ttsPcmBuffer = _ttsOutStream.GetPcmBuffer();
                        _ttsOutStream.Clear();
                        _ttsReady = false;
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
            //Log.LogDebug($"RTP packet received for {mediaType}.");

            if (mediaType == SDPMediaTypesEnum.audio)
            {
                //RenderAudio(rtpPacket);
            }
        }

        /// <summary>
        /// Render an audio RTP packet received from a remote party.
        /// </summary>
        /// <param name="rtpPacket">The RTP packet containing the audio payload.</param>
        //private void RenderAudio(RTPPacket rtpPacket)
        //{
        //    if (_waveProvider != null)
        //    {
        //        var sample = rtpPacket.Payload;

        //        for (int index = 0; index < sample.Length; index++)
        //        {
        //            short pcm = 0;

        //            if (rtpPacket.Header.PayloadType == (int)SDPMediaFormatsEnum.PCMA)
        //            {
        //                pcm = NAudio.Codecs.ALawDecoder.ALawToLinearSample(sample[index]);
        //                byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
        //                _waveProvider.AddSamples(pcmSample, 0, 2);
        //            }
        //            else
        //            {
        //                pcm = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(sample[index]);
        //                byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
        //                _waveProvider.AddSamples(pcmSample, 0, 2);
        //            }
        //        }
        //    }
        //}
    }
}
