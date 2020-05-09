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
    class TtsAudioOutStream : PushAudioOutputStreamCallback
    {
        public MemoryStream ms = new MemoryStream();
        int offSet = 0;

        public override uint Write(byte[] dataBuffer)
        {
            Console.WriteLine($"Bytes written to output stream {dataBuffer.Length}.");

            ms.Write(dataBuffer, 0, dataBuffer.Length);
            offSet = offSet + dataBuffer.Length;

            return (uint)dataBuffer.Length;
        }

        public override void Close()
        {
            ms.Close();
            base.Close();
        }
    }

    public class RtpSpeechSession : RTPSession, IMediaSession
    {
        private const int AUDIO_SAMPLE_PERIOD_MILLISECONDS = 20;
        //private const int AUDIO_BYTES_PER_SAMPLE = 2;               // G722 uses 16 bit samples.
        private const int G722_BITS_PER_SAMPLE = 8;

        private static readonly int AUDIO_RTP_CLOCK_RATE = SDPMediaFormatInfo.GetRtpClockRate(SDPMediaFormatsEnum.G722);
        private static readonly int AUDIO_CLOCK_RATE = SDPMediaFormatInfo.GetClockRate(SDPMediaFormatsEnum.G722);
        private static readonly int SILENCE_PCM_BUFFER_LENGTH = AUDIO_CLOCK_RATE * AUDIO_SAMPLE_PERIOD_MILLISECONDS / 1000;

        private static short[] _silencePcmBuffer = new short[SILENCE_PCM_BUFFER_LENGTH];    // Zero buffer representing PCM silence.

        private uint _rtpAudioTimestampPeriod = 0;
        private SDPMediaFormat _sendingAudioFormat = null;
        private bool _isStarted = false;
        private bool _isClosed = false;
        private uint _rtpEventSsrc;
        private Timer _audioStreamTimer;

        private G722Codec _g722Codec;
        private G722CodecState _g722CodecState;

        private readonly ILogger _logger;
        private readonly IConfiguration _config;

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
                if(rtpEvent.EndOfEvent && rtpHeader.MarkerBit == 1)
                {
                    // Full event is contained in a single RTP packet.
                    _logger.LogDebug($"RTP event {rtpEvent.EventID}.");
                }
                else if(!rtpEvent.EndOfEvent)
                {
                    _logger.LogDebug($"RTP event {rtpEvent.EventID}.");
                    _rtpEventSsrc = rtpHeader.SyncSource;
                }
            }

            if (_rtpEventSsrc != 0 && rtpEvent.EndOfEvent)
            {
                //_logger.LogDebug($"RTP end of event {rtpEvent.EventID}.");
                _rtpEventSsrc = 0;
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

                base.Close(reason);

                _audioStreamTimer?.Dispose();
            }
        }

        /// <summary>
        /// Timer callback to send the RTP audio packets.
        /// </summary>
        private void SendRTPAudio(object state)
        {
            short[] pcmInput = _silencePcmBuffer;
            byte[] encoded = new byte[pcmInput.Length / 2];

            _g722Codec.Encode(_g722CodecState, encoded, pcmInput, pcmInput.Length);

            base.SendAudioFrame((uint)encoded.Length, Convert.ToInt32(_sendingAudioFormat.FormatID), encoded);
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
