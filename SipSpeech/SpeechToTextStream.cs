//-----------------------------------------------------------------------------
// Filename: SpeechToTextStream.cs
//
// Description: This is the backing class for the Azure speech recognizer service. 
// It needs to have the PCM 16Khz 16 bit audio samples pushed from the application. 
// The Azure SDK internals then call read to get the data.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 10 May 2020 Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;

namespace sipspeech
{
    /// <summary>
    /// This is the backing class for the Azure speech recognizer service. It needs to have the
    /// PCM 16Khz 16 bit audio samples pushed from the application. The Azure SDK internals then call 
    /// read to get the data.
    /// </summary>
    public class SpeechToTextStream : PullAudioInputStreamCallback
    {
        private const int MAX_BUFFER_QUEUE_LENGTH = 100;
        private const int MAX_GETSAMPLE_ATTEMPTS = 10;
        private const int NO_SAMPLE_TIMEOUT_MILLISECONDS = 100;

        private readonly ILogger _logger;

        private ConcurrentQueue<byte[]> _bufferQueue = new ConcurrentQueue<byte[]>();
        private ManualResetEvent _sampleReadyMre = new ManualResetEvent(false);
        private bool _closed;

        public SpeechToTextStream(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// This methods gets called by the application to add audio samples for the
        /// speech recognition task.
        /// </summary>
        /// <param name="sample">The PCM 16Khz 16bit audio samples.</param>
        public void WriteSample(byte[] sample)
        {
            if (!_closed)
            {
                _bufferQueue.Enqueue(sample);
                _sampleReadyMre.Set();

                while (_bufferQueue.Count > MAX_BUFFER_QUEUE_LENGTH)
                {
                    _logger.LogWarning("SpeechToTextAudioInStream queue exceeded max limit, dropping buffer.");
                    _bufferQueue.TryDequeue(out _);
                }
            }
        }

        /// <summary>
        /// This methods gets called by the Azure SDK internals when it requires a new audio sample.
        /// </summary>
        /// <param name="dataBuffer">The buffer to copy the output samples to.</param>
        /// <param name="size">The amount of data required in the buffer.</param>
        /// <returns>The number of bytes that were supplied.</returns>
        public override int Read(byte[] dataBuffer, uint size)
        {
            //_logger.LogDebug($"SpeechToTextAudioInStream read requested {size} bytes.");

            if (!_closed)
            {
                if (_bufferQueue.Count == 0)
                {
                    _sampleReadyMre.Reset();
                    _sampleReadyMre.WaitOne(NO_SAMPLE_TIMEOUT_MILLISECONDS);
                }

                int attempts = 0;

                // The mechanism below is a little bit tricky and could probably be improved.
                // The constraints are:
                // - Samples are being supplied as they arrive over the RTP connection.
                // - Samples reads are being requested by the Azure SDK at a separate (and probably different) rate.
                // - The size of the RTP samples and the size of the data requested by a read can be different.
                // - It was observed if the data was not supplied to the reader fast enough the speech recognizer would stop.
                // - If the available sample is smaller than the data requested it is better to supply it and not wait for more data.

                while (attempts < MAX_GETSAMPLE_ATTEMPTS && !_closed)
                {
                    if (_bufferQueue.TryDequeue(out var sample))
                    {
                        int count = (size > (sample.Length)) ? sample.Length : (int)size - sample.Length;
                        Buffer.BlockCopy(sample, 0, dataBuffer, 0, count);

                        //_logger.LogDebug($"SpeechToTextAudioInStream read {count} bytes.");

                        return count;
                    }
                    else
                    {
                        //_logger.LogDebug($"SpeechToTextAudioInStream failed to get a sample from queue.");
                        attempts++;
                    }
                }

                if (!_closed)
                {
                    _logger.LogWarning("SpeechToTextAudioInStream was unable to ready any data within the allotted period.");
                }
            }

            // We don't have any data but if we return 0 the stream recognizer will stop.
            // Instead return the number of bytes requested. This should result in the buffer detecting silence.
            // The hope is the next RTP audio sample will arrive to avoid the recognizer detecting a long pause.
            return (int)size;
        }

        /// <summary>
        /// Closes the stream. Only to be called when the speech recognizer instance is no longer required.
        /// </summary>
        public override void Close()
        {
            _closed = true;

            _bufferQueue.Clear();
            _sampleReadyMre.Set();

            base.Close();
        }
    }
}
