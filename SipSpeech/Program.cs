//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: Prototype to integrate a basic SIP call server with Azure's
// text-to-speech and speech-to-text services.
//
// Author(s):
// Aaron Clauson  (aaron@sipsorcery.com)
// 
// History:
// 09 May 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;

namespace sipspeech
{
    struct SIPRegisterAccount
    {
        public string Username;
        public string Password;
        public string Domain;
        public int Expiry;

        public SIPRegisterAccount(string username, string password, string domain, int expiry)
        {
            Username = username;
            Password = password;
            Domain = domain;
            Expiry = expiry;
        }
    }

    class Program
    {
        private const string APP_CONFIG_FILE = "appsettings.json";
        private const string MAIN_LOGGER_PREFIX = "main";
        private const int SIP_LISTEN_PORT = 5060;

        private static Microsoft.Extensions.Logging.ILogger logger;
        private static ServiceProvider _serviceProvider;

        /// <summary>
        /// The set of SIP accounts available for registering and/or authenticating calls.
        /// </summary>
        private static readonly List<SIPRegisterAccount> _sipAccounts = new List<SIPRegisterAccount>
        {
            new SIPRegisterAccount( "softphonesample", "password", "sipsorcery.com", 120)
        };

        private static SIPTransport _sipTransport;

        /// <summary>
        /// Keeps track of the current active calls. It includes both received and placed calls.
        /// </summary>
        private static ConcurrentDictionary<string, SIPUserAgent> _calls = new ConcurrentDictionary<string, SIPUserAgent>();

        /// <summary>
        /// Keeps track of the SIP account registrations.
        /// </summary>
        private static ConcurrentDictionary<string, SIPRegistrationUserAgent> _registrations = new ConcurrentDictionary<string, SIPRegistrationUserAgent>();

        static async Task Main()
        {
            // Plumbing to set up logging, config and dependency injection.
            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(APP_CONFIG_FILE, false, false)
                .Build();

            var serilogConfig = new Serilog.LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console(theme: AnsiConsoleTheme.Code)
                .CreateLogger();

            _serviceProvider = new ServiceCollection()
                .AddLogging(loggingBuilder => 
                {
                    loggingBuilder.AddSerilog(serilogConfig);
                    loggingBuilder.SetMinimumLevel(LogLevel.Debug); 
                })
                .AddSingleton<IConfiguration>(config)
                .AddTransient<IMediaSession, RtpSpeechSession>()
                .BuildServiceProvider();

            logger = _serviceProvider.GetService<ILoggerFactory>().CreateLogger(MAIN_LOGGER_PREFIX);
            SIPSorcery.Sys.Log.LoggerFactory = _serviceProvider.GetService<ILoggerFactory>();

            Console.WriteLine("SIP Speech Server example.");
            Console.WriteLine("Press 'd' to send a random DTMF tone to the newest call.");
            Console.WriteLine("Press 'h' to hangup the oldest call.");
            Console.WriteLine("Press 'H' to hangup all calls.");
            Console.WriteLine("Press 'l' to list current calls.");
            Console.WriteLine("Press 'r' to list current registrations.");
            Console.WriteLine("Press 'q' to quit.");

            // Set up a default SIP transport.
            _sipTransport = new SIPTransport();
            _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SIP_LISTEN_PORT)));
            // If it's desired to listen on a single IP address use the equivalent of:
            //_sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Parse("192.168.11.50"), SIP_LISTEN_PORT)));
            EnableTraceLogs(_sipTransport, false);

            _sipTransport.SIPTransportRequestReceived += OnRequest;

            // Uncomment to enable registrations.
            //StartRegistrations(_sipTransport, _sipAccounts);

            CancellationTokenSource exitCts = new CancellationTokenSource();
            await Task.Run(() => OnKeyPress(exitCts.Token));

            logger.LogInformation("Exiting...");

            SIPSorcery.Net.DNSManager.Stop();

            if (_sipTransport != null)
            {
                logger.LogInformation("Shutting down SIP transport...");
                _sipTransport.Shutdown();
            }
        }

        /// <summary>
        /// Process user key presses.
        /// </summary>
        /// <param name="exit">The cancellation token to set if the user requests to quit the application.</param>
        private static async Task OnKeyPress(CancellationToken exit)
        {
            try
            {
                while (!exit.WaitHandle.WaitOne(0))
                {
                    var keyProps = Console.ReadKey();

                    if (keyProps.KeyChar == 'd')
                    {
                        if (_calls.Count == 0)
                        {
                            logger.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            var newestCall = _calls.OrderByDescending(x => x.Value.Dialogue.Inserted).First();
                            byte randomDtmf = (byte)Crypto.GetRandomInt(0, 15);
                            logger.LogInformation($"Sending DTMF {randomDtmf} to {newestCall.Key}.");
                            await newestCall.Value.SendDtmf(randomDtmf);
                        }
                    }
                    else if (keyProps.KeyChar == 'h')
                    {
                        if (_calls.Count == 0)
                        {
                            logger.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            var oldestCall = _calls.OrderBy(x => x.Value.Dialogue.Inserted).First();
                            logger.LogInformation($"Hanging up call {oldestCall.Key}.");
                            oldestCall.Value.OnCallHungup -= OnHangup;
                            oldestCall.Value.Hangup();
                            _calls.TryRemove(oldestCall.Key, out _);
                        }
                    }
                    else if (keyProps.KeyChar == 'H')
                    {
                        if (_calls.Count == 0)
                        {
                            logger.LogWarning("There are no active calls.");
                        }
                        else
                        {
                            foreach (var call in _calls)
                            {
                                logger.LogInformation($"Hanging up call {call.Key}.");
                                call.Value.OnCallHungup -= OnHangup;
                                call.Value.Hangup();
                            }
                            _calls.Clear();
                        }
                    }
                    else if (keyProps.KeyChar == 'l')
                    {
                        if (_calls.Count == 0)
                        {
                            logger.LogInformation("There are no active calls.");
                        }
                        else
                        {
                            logger.LogInformation("Current call list:");
                            foreach (var call in _calls)
                            {
                                int duration = Convert.ToInt32(DateTimeOffset.Now.Subtract(call.Value.Dialogue.Inserted).TotalSeconds);
                                uint rtpSent = (call.Value.MediaSession as RtpAudioSession).RtpPacketsSent;
                                uint rtpRecv = (call.Value.MediaSession as RtpAudioSession).RtpPacketsReceived;
                                logger.LogInformation($"{call.Key}: {call.Value.Dialogue.RemoteTarget} {duration}s {rtpSent}/{rtpRecv}");
                            }
                        }
                    }
                    else if (keyProps.KeyChar == 'r')
                    {
                        if (_registrations.Count == 0)
                        {
                            logger.LogInformation("There are no active registrations.");
                        }
                        else
                        {
                            logger.LogInformation("Current registration list:");
                            foreach (var registration in _registrations)
                            {
                                logger.LogInformation($"{registration.Key}: is registered {registration.Value.IsRegistered}, last attempt at {registration.Value.LastRegisterAttemptAt}");
                            }
                        }
                    }
                    else if (keyProps.KeyChar == 'q')
                    {
                        // Quit application.
                        logger.LogInformation("Quitting");
                        break;
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogError($"Exception OnKeyPress. {excp.Message}.");
            }
        }

        /// <summary>
        /// Event handler for receiving a DTMF tone.
        /// </summary>
        /// <param name="ua">The user agent that received the DTMF tone.</param>
        /// <param name="key">The DTMF tone.</param>
        /// <param name="duration">The duration in milliseconds of the tone.</param>
        private static void OnDtmfTone(SIPUserAgent ua, byte key, int duration)
        {
            string callID = ua.Dialogue.CallId;
            logger.LogInformation($"Call {callID} received DTMF tone {key}, duration {duration}ms.");
        }

        /// <summary>
        /// Because this is a server user agent the SIP transport must start listening for client user agents.
        /// </summary>
        private static async Task OnRequest(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
        {
            try
            {
                if (sipRequest.Header.From != null &&
                sipRequest.Header.From.FromTag != null &&
                sipRequest.Header.To != null &&
                sipRequest.Header.To.ToTag != null)
                {
                    // This is an in-dialog request that will be handled directly by a user agent instance.
                }
                else if (sipRequest.Method == SIPMethodsEnum.INVITE)
                {
                    logger.LogInformation($"Incoming call request: {localSIPEndPoint}<-{remoteEndPoint} {sipRequest.URI}.");

                    SIPUserAgent ua = new SIPUserAgent(_sipTransport, null);
                    ua.OnCallHungup += OnHangup;
                    ua.ServerCallCancelled += (uas) => logger.LogDebug("Incoming call cancelled by remote party.");
                    //ua.OnDtmfTone += (key, duration) => OnDtmfTone(ua, key, duration);
                    //ua.OnRtpEvent += (evt, hdr) => logger.LogDebug($"rtp event {evt.EventID}, duration {evt.Duration}, end of event {evt.EndOfEvent}, timestamp {hdr.Timestamp}, marker {hdr.MarkerBit}.");
                    ua.OnTransactionTraceMessage += (tx, msg) => logger.LogDebug($"uas tx {tx.TransactionId}: {msg}");
                    ua.ServerCallRingTimeout += (uas) =>
                    {
                        logger.LogWarning($"Incoming call timed out in {uas.ClientTransaction.TransactionState} state waiting for client ACK, terminating.");
                        ua.Hangup();
                    };

                    var uas = ua.AcceptCall(sipRequest);
                    var rtpSession = _serviceProvider.GetService<IMediaSession>();

                    // Insert a brief delay to allow testing of the "Ringing" progress response.
                    // Without the delay the call gets answered before it can be sent.
                    await Task.Delay(500);

                    await ua.Answer(uas, rtpSession);

                    if (ua.IsCallActive)
                    {
                        await rtpSession.Start();
                        _calls.TryAdd(ua.Dialogue.CallId, ua);
                    }
                }
                else if (sipRequest.Method == SIPMethodsEnum.BYE)
                {
                    SIPResponse byeResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, null);
                    await _sipTransport.SendResponseAsync(byeResponse);
                }
                else if (sipRequest.Method == SIPMethodsEnum.SUBSCRIBE)
                {
                    SIPResponse notAllowededResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                    await _sipTransport.SendResponseAsync(notAllowededResponse);
                }
                else if (sipRequest.Method == SIPMethodsEnum.OPTIONS || sipRequest.Method == SIPMethodsEnum.REGISTER)
                {
                    SIPResponse optionsResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                    await _sipTransport.SendResponseAsync(optionsResponse);
                }
            }
            catch (Exception reqExcp)
            {
                logger.LogWarning($"Exception handling {sipRequest.Method}. {reqExcp.Message}");
            }
        }

        /// <summary>
        /// Remove call from the active calls list.
        /// </summary>
        /// <param name="dialogue">The dialogue that was hungup.</param>
        private static void OnHangup(SIPDialogue dialogue)
        {
            if (dialogue != null)
            {
                string callID = dialogue.CallId;
                if (_calls.ContainsKey(callID))
                {
                    _calls.TryRemove(callID, out _);
                }
            }
        }

        /// <summary>
        /// Starts a registration agent for each of the supplied SIP accounts.
        /// </summary>
        /// <param name="sipTransport">The SIP transport to use for the registrations.</param>
        /// <param name="sipAccounts">The list of SIP accounts to create a registration for.</param>
        private static void StartRegistrations(SIPTransport sipTransport, List<SIPRegisterAccount> sipAccounts)
        {
            foreach (var sipAccount in sipAccounts)
            {
                var regUserAgent = new SIPRegistrationUserAgent(sipTransport, sipAccount.Username, sipAccount.Password, sipAccount.Domain, sipAccount.Expiry);

                // Event handlers for the different stages of the registration.
                regUserAgent.RegistrationFailed += (uri, err) => logger.LogError($"{uri.ToString()}: {err}");
                regUserAgent.RegistrationTemporaryFailure += (uri, msg) => logger.LogWarning($"{uri.ToString()}: {msg}");
                regUserAgent.RegistrationRemoved += (uri) => logger.LogError($"{uri.ToString()} registration failed.");
                regUserAgent.RegistrationSuccessful += (uri) => logger.LogInformation($"{uri.ToString()} registration succeeded.");

                // Start the thread to perform the initial registration and then periodically resend it.
                regUserAgent.Start();

                _registrations.TryAdd($"{sipAccount.Username}@{sipAccount.Domain}", regUserAgent);
            }
        }

        /// <summary>
        /// Enable detailed SIP log messages.
        /// </summary>
        private static void EnableTraceLogs(SIPTransport sipTransport, bool fullSIP)
        {
            sipTransport.SIPRequestInTraceEvent += (localEP, remoteEP, req) =>
            {
                logger.LogDebug($"Request received: {localEP}<-{remoteEP}");

                if (!fullSIP)
                {
                    logger.LogDebug(req.StatusLine);
                }
                else
                {
                    logger.LogDebug(req.ToString());
                }
            };

            sipTransport.SIPRequestOutTraceEvent += (localEP, remoteEP, req) =>
            {
                logger.LogDebug($"Request sent: {localEP}->{remoteEP}");

                if (!fullSIP)
                {
                    logger.LogDebug(req.StatusLine);
                }
                else
                {
                    logger.LogDebug(req.ToString());
                }
            };

            sipTransport.SIPResponseInTraceEvent += (localEP, remoteEP, resp) =>
            {
                logger.LogDebug($"Response received: {localEP}<-{remoteEP}");

                if (!fullSIP)
                {
                    logger.LogDebug(resp.ShortDescription);
                }
                else
                {
                    logger.LogDebug(resp.ToString());
                }
            };

            sipTransport.SIPResponseOutTraceEvent += (localEP, remoteEP, resp) =>
            {
                logger.LogDebug($"Response sent: {localEP}->{remoteEP}");

                if (!fullSIP)
                {
                    logger.LogDebug(resp.ShortDescription);
                }
                else
                {
                    logger.LogDebug(resp.ToString());
                }
            };

            sipTransport.SIPRequestRetransmitTraceEvent += (tx, req, count) =>
            {
                logger.LogDebug($"Request retransmit {count} for request {req.StatusLine}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };

            sipTransport.SIPResponseRetransmitTraceEvent += (tx, resp, count) =>
            {
                logger.LogDebug($"Response retransmit {count} for response {resp.ShortDescription}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };
        }
    }
}
