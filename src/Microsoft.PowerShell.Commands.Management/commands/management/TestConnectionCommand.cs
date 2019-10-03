// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// The implementation of the "Test-Connection" cmdlet.
    /// </summary>
    [Cmdlet(VerbsDiagnostic.Test, "Connection", DefaultParameterSetName = DefaultPingParameterSet,
        HelpUri = "https://go.microsoft.com/fwlink/?LinkID=135266")]
    [OutputType(typeof(PingStatus), ParameterSetName = new string[] { DefaultPingParameterSet })]
    [OutputType(typeof(PingReply), ParameterSetName = new string[] { RepeatPingParameterSet, MtuSizeDetectParameterSet })]
    [OutputType(typeof(bool), ParameterSetName = new string[] { DefaultPingParameterSet, RepeatPingParameterSet, TcpPortParameterSet })]
    [OutputType(typeof(int), ParameterSetName = new string[] { MtuSizeDetectParameterSet })]
    [OutputType(typeof(TraceStatus), ParameterSetName = new string[] { TraceRouteParameterSet })]
    public class TestConnectionCommand : PSCmdlet, IDisposable
    {
        #region Parameter Set Names
        private const string DefaultPingParameterSet = "DefaultPing";
        private const string RepeatPingParameterSet = "RepeatPing";
        private const string TraceRouteParameterSet = "TraceRoute";
        private const string TcpPortParameterSet = "TcpPort";
        private const string MtuSizeDetectParameterSet = "MtuSizeDetect";

        #endregion

        #region Cmdlet Defaults

        // Count of pings sent to each trace route hop. Default mimics Windows' defaults.
        // If this value changes, we need to update 'ConsoleTraceRouteReply' resource string.
        private const int DefaultTraceRoutePingCount = 3;

        // Default size for the send buffer.
        private const int DefaultSendBufferSize = 32;

        private const string TestConnectionExceptionId = "TestConnectionException";

        #endregion

        #region Private Fields

        private static byte[] s_DefaultSendBuffer = null;

        private bool _disposed = false;

        private readonly Ping _sender = new Ping();

        private readonly ManualResetEventSlim _pingComplete = new ManualResetEventSlim();

        private PingCompletedEventArgs _pingCompleteArgs;

        #endregion

        #region Parameters

        /// <summary>
        /// Gets or sets whether to do ping test.
        /// Default is true.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        public SwitchParameter Ping { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to force use of IPv4 protocol.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [Parameter(ParameterSetName = TraceRouteParameterSet)]
        [Parameter(ParameterSetName = MtuSizeDetectParameterSet)]
        [Parameter(ParameterSetName = TcpPortParameterSet)]
        public SwitchParameter IPv4 { get; set; }

        /// <summary>
        /// Gets or sets whether to force use of IPv6 protocol.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [Parameter(ParameterSetName = TraceRouteParameterSet)]
        [Parameter(ParameterSetName = MtuSizeDetectParameterSet)]
        [Parameter(ParameterSetName = TcpPortParameterSet)]
        public SwitchParameter IPv6 { get; set; }

        /// <summary>
        /// Gets or sets whether to do reverse DNS lookup to get names for IP addresses.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [Parameter(ParameterSetName = TraceRouteParameterSet)]
        [Parameter(ParameterSetName = MtuSizeDetectParameterSet)]
        [Parameter(ParameterSetName = TcpPortParameterSet)]
        public SwitchParameter ResolveDestination { get; set; }

        /// <summary>
        /// Gets the source from which to run the selected test.
        /// The default is localhost.
        /// Remoting is not yet implemented internally in the cmdlet.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [Parameter(ParameterSetName = TraceRouteParameterSet)]
        [Parameter(ParameterSetName = TcpPortParameterSet)]
        public string Source { get; } = Dns.GetHostName();

        /// <summary>
        /// Gets or sets the number of times the Ping data packets can be forwarded by routers.
        /// As gateways and routers transmit packets through a network, they decrement the Time-to-Live (TTL)
        /// value found in the packet header.
        /// The default (from Windows) is 128 hops.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [Parameter(ParameterSetName = TraceRouteParameterSet)]
        [ValidateRange(0, sMaxHops)]
        [Alias("Ttl", "TimeToLive", "Hops")]
        public int MaxHops { get; set; } = sMaxHops;

        private const int sMaxHops = 128;

        /// <summary>
        /// Gets or sets the number of ping attempts.
        /// The default (from Windows) is 4 times.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [ValidateRange(ValidateRangeKind.Positive)]
        public int Count { get; set; } = 4;

        /// <summary>
        /// Gets or sets the delay between ping attempts.
        /// The default (from Windows) is 1 second.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [ValidateRange(ValidateRangeKind.Positive)]
        public int Delay { get; set; } = 1;

        /// <summary>
        /// Gets or sets the buffer size to send with the ping packet.
        /// The default (from Windows) is 32 bytes.
        /// Max value is 65500 (limitation imposed by Windows API).
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        [Alias("Size", "Bytes", "BS")]
        [ValidateRange(0, 65500)]
        public int BufferSize { get; set; } = DefaultSendBufferSize;

        /// <summary>
        /// Gets or sets whether to prevent fragmentation of the ICMP packets.
        /// Currently CoreFX not supports this on Unix.
        /// </summary>
        [Parameter(ParameterSetName = DefaultPingParameterSet)]
        [Parameter(ParameterSetName = RepeatPingParameterSet)]
        public SwitchParameter DontFragment { get; set; }

        /// <summary>
        /// Gets or sets whether to continue pinging until user presses Ctrl-C (or Int.MaxValue threshold reached).
        /// </summary>
        [Parameter(Mandatory = true, ParameterSetName = RepeatPingParameterSet)]
        [Alias("Continues", "Continuous")]
        public SwitchParameter Repeat { get; set; }

        /// <summary>
        /// Gets or sets whether to enable quiet output mode, reducing output to a single simple value only.
        /// By default, objects are emitted.
        /// With this switch, standard ping and -Traceroute returns only true / false, and -MtuSize returns an integer.
        /// </summary>
        [Parameter]
        public SwitchParameter Quiet;

        /// <summary>
        /// Gets or sets the timeout value for an individual ping in seconds.
        /// If a response is not received in this time, no response is assumed.
        /// The default (from Windows) is 5 seconds.
        /// </summary>
        [Parameter]
        [ValidateRange(ValidateRangeKind.Positive)]
        public int TimeoutSeconds { get; set; } = 5;

        /// <summary>
        /// Gets or sets the destination hostname or IP address.
        /// </summary>
        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        [Alias("ComputerName")]
        public string[] TargetName { get; set; }

        /// <summary>
        /// Gets or sets whether to detect Maximum Transmission Unit size.
        /// When selected, only a single ping result is returned, indicating the maximum buffer size
        /// the route to the destination can support without fragmenting the ICMP packets.
        /// </summary>
        [Parameter(Mandatory = true, ParameterSetName = MtuSizeDetectParameterSet)]
        [Alias("MtuSizeDetect")]
        public SwitchParameter MtuSize { get; set; }

        /// <summary>
        /// Gets or sets whether to perform a traceroute test.
        /// </summary>
        [Parameter(Mandatory = true, ParameterSetName = TraceRouteParameterSet)]
        public SwitchParameter Traceroute { get; set; }

        /// <summary>
        /// Gets or sets whether to perform a TCP connection test.
        /// </summary>
        [ValidateRange(0, 65535)]
        [Parameter(Mandatory = true, ParameterSetName = TcpPortParameterSet)]
        public int TcpPort { get; set; }

        #endregion Parameters

        /// <summary>
        /// BeginProcessing implementation for TestConnectionCommand.
        /// </summary>
        protected override void BeginProcessing()
        {
            // Add the event handler to the PingCompleted event, to inform the cmdlet when pings are completed.
            _sender.PingCompleted += OnPingComplete;

            switch (ParameterSetName)
            {
                case RepeatPingParameterSet:
                    Count = int.MaxValue;
                    break;
            }
        }

        /// <summary>
        /// Process a connection test.
        /// </summary>
        protected override void ProcessRecord()
        {
            foreach (var targetName in TargetName)
            {
                switch (ParameterSetName)
                {
                    case DefaultPingParameterSet:
                    case RepeatPingParameterSet:
                        ProcessPing(targetName);
                        break;
                    case MtuSizeDetectParameterSet:
                        ProcessMTUSize(targetName);
                        break;
                    case TraceRouteParameterSet:
                        ProcessTraceroute(targetName);
                        break;
                    case TcpPortParameterSet:
                        ProcessConnectionByTCPPort(targetName);
                        break;
                }
            }
        }

        /// <summary>
        /// On receiving the StopProcessing() request, the cmdlet will immediately cancel any in-progress ping request.
        /// This allows a cancellation to occur during a ping request without having to wait for the timeout.
        /// </summary>
        protected override void StopProcessing()
        {
            _sender?.SendAsyncCancel();
        }

        #region ConnectionTest

        private void ProcessConnectionByTCPPort(string targetNameOrAddress)
        {
            if (!InitProcessPing(targetNameOrAddress, out string resolvedTargetName, out IPAddress targetAddress))
            {
                return;
            }

            TcpClient client = new TcpClient();

            try
            {
                Task connectionTask = client.ConnectAsync(targetAddress, TcpPort);
                string targetString = targetAddress.ToString();

                for (var i = 1; i <= TimeoutSeconds; i++)
                {
                    Task timeoutTask = Task.Delay(millisecondsDelay: 1000);
                    Task.WhenAny(connectionTask, timeoutTask).Result.Wait();

                    if (timeoutTask.Status == TaskStatus.Faulted || timeoutTask.Status == TaskStatus.Canceled)
                    {
                        // Waiting is interrupted by Ctrl-C.
                        WriteObject(false);
                        return;
                    }

                    if (connectionTask.Status == TaskStatus.RanToCompletion)
                    {
                        WriteObject(true);
                        return;
                    }
                }
            }
            catch
            {
                // Silently ignore connection errors.
            }
            finally
            {
                client.Close();
            }

            WriteObject(false);
        }
        #endregion ConnectionTest

        #region TracerouteTest
        private void ProcessTraceroute(string targetNameOrAddress)
        {
            byte[] buffer = GetSendBuffer(BufferSize);

            if (!InitProcessPing(targetNameOrAddress, out string resolvedTargetName, out IPAddress targetAddress))
            {
                return;
            }

            int currentHop = 1;
            PingOptions pingOptions = new PingOptions(currentHop, DontFragment.IsPresent);
            PingReply reply = null;
            int timeout = TimeoutSeconds * 1000;
            var timer = new Stopwatch();

            do
            {
                // Clear the stored router name for every hop
                string routerName = null;
                pingOptions.Ttl = currentHop;
                currentHop++;

                // In traceroutes we don't use 'Count' parameter.
                // If we change 'DefaultTraceRoutePingCount' we should change 'ConsoleTraceRouteReply' resource string.
                for (uint i = 1; i <= DefaultTraceRoutePingCount; i++)
                {
                    TraceStatus hopResult;
                    try
                    {
                        reply = SendCancellablePing(targetAddress, timeout, buffer, pingOptions, timer);

                        // Only get router name if we haven't already retrieved it
                        if (routerName == null)
                        {
                            if (ResolveDestination.IsPresent)
                            {
                                try
                                {
                                    routerName = reply.Status == IPStatus.Success
                                        ? Dns.GetHostEntry(reply.Address).HostName
                                        : reply.Address?.ToString();
                                }
                                catch
                                {
                                    // Swallow hostname resolution errors and continue with trace
                                }
                            }
                            else
                            {
                                routerName = reply.Address?.ToString();
                            }
                        }

                        var status = new PingStatus(
                            Source,
                            routerName,
                            reply,
                            pingOptions,
                            latency: reply.Status == IPStatus.Success
                                ? reply.RoundtripTime
                                : timer.ElapsedMilliseconds,
                            buffer.Length,
                            pingNum: i);
                        hopResult = new TraceStatus(currentHop, status, Source, resolvedTargetName, targetAddress);

                        if (!Quiet.IsPresent)
                        {
                            WriteObject(hopResult);
                        }

                        timer.Reset();
                    }
                    catch (PingException ex)
                    {
                        string message = StringUtil.Format(
                            TestConnectionResources.NoPingResult,
                            resolvedTargetName,
                            ex.Message);
                        Exception pingException = new PingException(message, ex.InnerException);
                        ErrorRecord errorRecord = new ErrorRecord(
                            pingException,
                            TestConnectionExceptionId,
                            ErrorCategory.ResourceUnavailable,
                            resolvedTargetName);
                        WriteError(errorRecord);

                        continue;
                    }

                    // We use short delay because it is impossible DoS with trace route.
                    Thread.Sleep(50);
                }
            } while (reply != null
                && currentHop <= sMaxHops
                && (reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.TimedOut));

            if (Quiet.IsPresent)
            {
                WriteObject(currentHop <= sMaxHops);
            }
        }

        #endregion TracerouteTest

        #region MTUSizeTest
        private void ProcessMTUSize(string targetNameOrAddress)
        {
            PingReply reply, replyResult = null;
            if (!InitProcessPing(targetNameOrAddress, out string resolvedTargetName, out IPAddress targetAddress))
            {
                return;
            }

            WriteVerbose(StringUtil.Format(
                TestConnectionResources.MTUSizeDetectStart,
                resolvedTargetName,
                targetAddress.ToString(),
                BufferSize));

            // Caution! Algorithm is sensitive to changing boundary values.
            int HighMTUSize = 10000;
            int CurrentMTUSize = 1473;
            int LowMTUSize = targetAddress.AddressFamily == AddressFamily.InterNetworkV6 ? 1280 : 68;
            int timeout = TimeoutSeconds * 1000;

            try
            {
                PingOptions pingOptions = new PingOptions(MaxHops, true);
                int retry = 1;

                while (LowMTUSize < (HighMTUSize - 1))
                {
                    byte[] buffer = GetSendBuffer(CurrentMTUSize);

                    WriteDebug(StringUtil.Format(
                        "LowMTUSize: {0}, CurrentMTUSize: {1}, HighMTUSize: {2}",
                        LowMTUSize,
                        CurrentMTUSize,
                        HighMTUSize));

                    reply = SendCancellablePing(targetAddress, timeout, buffer, pingOptions);

                    if (reply.Status == IPStatus.PacketTooBig)
                    {
                        HighMTUSize = CurrentMTUSize;
                        retry = 1;
                    }
                    else if (reply.Status == IPStatus.Success)
                    {
                        LowMTUSize = CurrentMTUSize;
                        replyResult = reply;
                        retry = 1;
                    }
                    else
                    {
                        // If the host did't reply, try again up to the 'Count' value.
                        if (retry >= Count)
                        {
                            string message = StringUtil.Format(
                                TestConnectionResources.NoPingResult,
                                targetAddress,
                                reply.Status.ToString());
                            Exception pingException = new PingException(message);
                            ErrorRecord errorRecord = new ErrorRecord(
                                pingException,
                                TestConnectionExceptionId,
                                ErrorCategory.ResourceUnavailable,
                                targetAddress);
                            WriteError(errorRecord);
                            return;
                        }
                        else
                        {
                            retry++;
                            continue;
                        }
                    }

                    CurrentMTUSize = (LowMTUSize + HighMTUSize) / 2;

                    // Prevent DoS attack.
                    Thread.Sleep(100);
                }
            }
            catch (PingException ex)
            {
                string message = StringUtil.Format(TestConnectionResources.NoPingResult, targetAddress, ex.Message);
                Exception pingException = new PingException(message, ex.InnerException);
                ErrorRecord errorRecord = new ErrorRecord(
                    pingException,
                    TestConnectionExceptionId,
                    ErrorCategory.ResourceUnavailable,
                    targetAddress);
                WriteError(errorRecord);
                return;
            }

            if (Quiet.IsPresent)
            {
                WriteObject(CurrentMTUSize);
            }
            else
            {
                WriteObject(new PingMtuStatus(Source, targetAddress.ToString(), replyResult));
            }
        }

        #endregion MTUSizeTest

        #region PingTest

        private void ProcessPing(string targetNameOrAddress)
        {
            if (!InitProcessPing(targetNameOrAddress, out string resolvedTargetName, out IPAddress targetAddress))
            {
                return;
            }

            bool quietResult = true;
            byte[] buffer = GetSendBuffer(BufferSize);

            PingReply reply;
            PingOptions pingOptions = new PingOptions(MaxHops, DontFragment.IsPresent);
            int timeout = TimeoutSeconds * 1000;
            int delay = Delay * 1000;

            for (uint i = 1; i <= Count; i++)
            {
                try
                {
                    reply = SendCancellablePing(targetAddress, timeout, buffer, pingOptions);
                }
                catch (PingException ex)
                {
                    string message = StringUtil.Format(TestConnectionResources.NoPingResult, resolvedTargetName, ex.Message);
                    Exception pingException = new PingException(message, ex.InnerException);
                    ErrorRecord errorRecord = new ErrorRecord(
                        pingException,
                        TestConnectionExceptionId,
                        ErrorCategory.ResourceUnavailable,
                        resolvedTargetName);
                    WriteError(errorRecord);

                    quietResult = false;
                    continue;
                }

                if (Repeat.IsPresent)
                {
                    WriteObject(reply);
                }
                else if (Quiet.IsPresent)
                {
                    // Return 'true' only if all pings have completed successfully.
                    quietResult &= reply.Status == IPStatus.Success;
                }
                else
                {
                    WriteObject(new PingStatus(Source, resolvedTargetName, reply, i));
                }

                // Delay between pings, but not after last ping.
                if (i < Count && Delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }

            if (Quiet.IsPresent)
            {
                WriteObject(quietResult);
            }
        }

        #endregion PingTest

        private bool InitProcessPing(string targetNameOrAddress, out string resolvedTargetName, out IPAddress targetAddress)
        {
            resolvedTargetName = targetNameOrAddress;

            IPHostEntry hostEntry;
            if (IPAddress.TryParse(targetNameOrAddress, out targetAddress))
            {
                if (ResolveDestination)
                {
                    hostEntry = Dns.GetHostEntry(targetNameOrAddress);
                    resolvedTargetName = hostEntry.HostName;
                }
            }
            else
            {
                try
                {
                    hostEntry = Dns.GetHostEntry(targetNameOrAddress);

                    if (ResolveDestination)
                    {
                        resolvedTargetName = hostEntry.HostName;
                        hostEntry = Dns.GetHostEntry(hostEntry.HostName);
                    }
                }
                catch (Exception ex)
                {
                    string message = StringUtil.Format(
                        TestConnectionResources.NoPingResult,
                        resolvedTargetName,
                        TestConnectionResources.CannotResolveTargetName);
                    Exception pingException = new PingException(message, ex);
                    ErrorRecord errorRecord = new ErrorRecord(
                        pingException,
                        TestConnectionExceptionId,
                        ErrorCategory.ResourceUnavailable,
                        resolvedTargetName);
                    WriteError(errorRecord);
                    return false;
                }

                if (IPv6 || IPv4)
                {
                    AddressFamily addressFamily = IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

                    foreach (var address in hostEntry.AddressList)
                    {
                        if (address.AddressFamily == addressFamily)
                        {
                            targetAddress = address;
                            break;
                        }
                    }

                    if (targetAddress == null)
                    {
                        string message = StringUtil.Format(
                            TestConnectionResources.NoPingResult,
                            resolvedTargetName,
                            TestConnectionResources.TargetAddressAbsent);
                        Exception pingException = new PingException(message, null);
                        ErrorRecord errorRecord = new ErrorRecord(
                            pingException,
                            TestConnectionExceptionId,
                            ErrorCategory.ResourceUnavailable,
                            resolvedTargetName);
                        WriteError(errorRecord);
                        return false;
                    }
                }
                else
                {
                    targetAddress = hostEntry.AddressList[0];
                }
            }

            return true;
        }

        // Users most often use the default buffer size so we cache the buffer.
        // Creates and fills a send buffer. This follows the ping.exe and CoreFX model.
        private byte[] GetSendBuffer(int bufferSize)
        {
            if (bufferSize == DefaultSendBufferSize && s_DefaultSendBuffer != null)
            {
                return s_DefaultSendBuffer;
            }

            byte[] sendBuffer = new byte[bufferSize];

            for (int i = 0; i < bufferSize; i++)
            {
                sendBuffer[i] = (byte)((int)'a' + i % 23);
            }

            if (bufferSize == DefaultSendBufferSize && s_DefaultSendBuffer == null)
            {
                s_DefaultSendBuffer = sendBuffer;
            }

            return sendBuffer;
        }

        /// <summary>
        /// IDisposable implementation, dispose of any disposable resources created by the cmdlet.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Implementation of IDisposable for both manual Dispose() and finalizer-called disposal of resources.
        /// </summary>
        /// <param name="disposing">
        /// Specified as true when Dispose() was called, false if this is called from the finalizer.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _sender.Dispose();
                    _pingComplete?.Dispose();
                }

                _disposed = true;
            }
        }

        // Uses the SendAsync() method to send pings, so that Ctrl+C can halt the request early if needed.
        private PingReply SendCancellablePing(
            IPAddress targetAddress,
            int timeout,
            byte[] buffer,
            PingOptions pingOptions,
            Stopwatch timer = null)
        {
            timer?.Start();
            _sender.SendAsync(targetAddress, timeout, buffer, pingOptions, this);
            _pingComplete.Wait();
            timer?.Stop();

            // Pause to let _sender's async flags to be reset properly so the next SendAsync call doesn't fail.
            Thread.Sleep(2);

            if (_pingCompleteArgs.Cancelled)
            {
                // The only cancellation we have implemented is on pipeline stops via StopProcessing().
                throw new PipelineStoppedException();
            }

            if (_pingCompleteArgs.Error != null)
            {
                throw new PingException(_pingCompleteArgs.Error.Message, _pingCompleteArgs.Error);
            }

            return _pingCompleteArgs.Reply;
        }

        // This event is triggered when the ping is completed, and passes along the eventargs so that we know
        // if the ping was cancelled, or an exception was thrown.
        private static void OnPingComplete(object sender, PingCompletedEventArgs e)
        {
            ((TestConnectionCommand)e.UserState)._pingCompleteArgs = e;
            ((TestConnectionCommand)e.UserState)._pingComplete.Set();
        }

        /// <summary>
        /// The class contains information about the source, the destination and ping results.
        /// </summary>
        public class PingStatus
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="PingStatus"/> class.
            /// This constructor allows manually specifying the initial values for the cases where the PingReply
            /// object may be missing some information, specifically in the instances where PingReply objects are
            /// utilised to perform a traceroute.
            /// </summary>
            /// <param name="source">The source machine name or IP of the ping.</param>
            /// <param name="destination">The destination machine name of the ping.</param>
            /// <param name="reply">The response from the ping attempt.</param>
            /// <param name="options">The PingOptions specified when the ping was sent.</param>
            /// <param name="latency">The latency of the ping.</param>
            /// <param name="bufferSize">The buffer size.</param>
            /// <param name="pingNum">The sequence number in the sequence of pings to the hop point.</param>
            internal PingStatus(
                string source,
                string destination,
                PingReply reply,
                PingOptions options,
                long latency,
                int bufferSize,
                uint pingNum)
                : this(source, destination, reply, pingNum)
            {
                _options = options;
                _bufferSize = bufferSize;
                _latency = latency;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="PingStatus"/> class.
            /// </summary>
            /// <param name="source">The source machine name or IP of the ping.</param>
            /// <param name="destination">The destination machine name of the ping.</param>
            /// <param name="reply">The response from the ping attempt.</param>
            /// <param name="pingNum">The sequence number of the ping in the sequence of pings to the target.</param>
            internal PingStatus(string source, string destination, PingReply reply, uint pingNum)
            {
                Ping = pingNum;
                Reply = reply;
                Source = source;
                Destination = destination ?? reply.Address.ToString();
            }

            // These values should only be set if this PingStatus was created as part of a traceroute.
            private readonly int _bufferSize = -1;
            private readonly PingOptions _options;
            private readonly long _latency = -1;

            /// <summary>
            /// Gets the sequence number of this ping in the sequence of pings to the <see cref="Destination"/>
            /// </summary>
            public uint Ping { get; }

            /// <summary>
            /// Gets the source from which the ping was sent.
            /// </summary>
            public string Source { get; }

            /// <summary>
            /// Gets the destination which was pinged.
            /// </summary>
            public string Destination { get; }

            /// <summary>
            /// Gets the target address of the ping.
            /// </summary>
            public IPAddress Address { get => Reply.Status == IPStatus.Success ? Reply.Address : null; }

            /// <summary>
            /// Gets the roundtrip time of the ping in milliseconds.
            /// </summary>
            public long Latency { get => _latency >= 0 ? _latency : Reply.RoundtripTime; }

            /// <summary>
            /// Gets the returned status of the ping.
            /// </summary>
            public IPStatus Status { get => Reply.Status; }

            /// <summary>
            /// Gets the size in bytes of the buffer data sent in the ping.
            /// </summary>
            public int BufferSize { get => _bufferSize >= 0 ? _bufferSize : Reply.Buffer.Length; }

            /// <summary>
            /// Gets the reply object from this ping.
            /// </summary>
            public PingReply Reply { get; }

            /// <summary>
            /// Gets the options used when sending the ping.
            /// </summary>
            public PingOptions Options { get => _options ?? Reply.Options; }
        }

        /// <summary>
        /// The class contains information about the source, the destination and ping results.
        /// </summary>
        public class PingMtuStatus : PingStatus
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="PingMtuStatus"/> class.
            /// </summary>
            /// <param name="source">The source machine name or IP of the ping.</param>
            /// <param name="destination">The destination machine name of the ping.</param>
            /// <param name="reply">The response from the ping attempt.</param>
            internal PingMtuStatus(string source, string destination, PingReply reply)
                : base(source, destination, reply, 1)
            {
            }

            /// <summary>
            /// Gets the maximum transmission unit size on the network path between the source and destination.
            /// </summary>
            public int MtuSize { get => BufferSize; }
        }

        /// <summary>
        /// The class contains an information about a trace route attempt.
        /// </summary>
        public class TraceStatus
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TraceStatus"/> class.
            /// </summary>
            /// <param name="hop">The hop number of this trace hop.</param>
            /// <param name="status">The PingStatus response from this trace hop.</param>
            /// <param name="source">The source computer name or IP address of the traceroute.</param>
            /// <param name="destination">The target destination of the traceroute.</param>
            /// <param name="destinationAddress">The target IPAddress of the overall traceroute.</param>
            internal TraceStatus(
                int hop,
                PingStatus status,
                string source,
                string destination,
                IPAddress destinationAddress)
            {
                _status = status;
                Hop = hop;
                Source = source;
                Target = destination;
                TargetAddress = destinationAddress;
            }

            private readonly PingStatus _status;

            /// <summary>
            /// Gets the number of the current hop / router.
            /// </summary>
            public int Hop { get; }

            /// <summary>
            /// Gets the hostname of the current hop point.
            /// </summary>
            /// <value></value>
            public string Hostname
            {
                get => _status.Destination != "0.0.0.0"
                    ? _status.Destination
                    : null;
            }

            /// <summary>
            /// Gets the sequence number of the ping in the sequence of pings to the hop point.
            /// </summary>
            public uint Ping { get => _status.Ping; }

            /// <summary>
            /// Gets the IP address of the current hop point.
            /// </summary>
            public IPAddress HopAddress { get => _status.Address; }

            /// <summary>
            /// Gets the latency values of each ping to the current hop point.
            /// </summary>
            public long Latency { get => _status.Latency; }

            /// <summary>
            /// Gets the status of the traceroute hop.
            /// It is considered successful if the individual ping reports either Success or TtlExpired;
            /// TtlExpired is the expected response from an intermediate traceroute hop.
            /// </summary>
            public IPStatus Status
            {
                get => _status.Status == IPStatus.TtlExpired
                    ? IPStatus.Success
                    : _status.Status;
            }

            /// <summary>
            /// Gets the source address of the traceroute command.
            /// </summary>
            public string Source { get; }

            /// <summary>
            /// Gets the final destination hostname of the trace.
            /// </summary>
            public string Target { get; }

            /// <summary>
            /// Gets the final destination IP address of the trace.
            /// </summary>
            public IPAddress TargetAddress { get; }

            /// <summary>
            /// Gets the raw PingReply object received from the ping to this hop point.
            /// </summary>
            public PingReply Reply { get => _status.Reply; }

            /// <summary>
            /// Gets the PingOptions used to send the ping to the trace hop.
            /// </summary>
            public PingOptions Options { get => _status.Options; }
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="TestConnectionCommand"/> class.
        /// </summary>
        ~TestConnectionCommand()
        {
            Dispose(disposing: false);
        }
    }
}
