using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Configuration.Install;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NT230.R12.ANTT_Lab2.ReverseShell
{
    public partial class ReverseShellService : ServiceBase
    {
        internal const string InstalledServiceName = "NT230.R12.ANTT_Lab2.ReverseShell";

        private CancellationTokenSource cancellation;
        private Task workerTask;
        private LabLogger logger;

        public ReverseShellService()
        {
            InitializeComponent();
            ServiceName = InstalledServiceName;
            CanStop = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            LabConfiguration configuration = LabConfiguration.FromAppSettings();
            logger = new LabLogger(configuration.LogPath);
            cancellation = new CancellationTokenSource();

            ReverseConnectionWorker worker = new ReverseConnectionWorker(configuration, logger);
            workerTask = Task.Run(() => worker.RunAsync(cancellation.Token));
            workerTask.ContinueWith(
                task => logger.Write("Worker faulted: " + task.Exception.GetBaseException().Message),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            logger.Write("Service started.");
        }

        protected override void OnStop()
        {
            CancellationTokenSource localCancellation = cancellation;
            Task localWorker = workerTask;

            if (localCancellation != null)
            {
                localCancellation.Cancel();
            }

            if (localWorker != null)
            {
                try
                {
                    bool stopped = localWorker.Wait(TimeSpan.FromSeconds(10));
                    if (!stopped && logger != null)
                    {
                        logger.Write("Worker did not stop within 10 seconds.");
                    }
                }
                catch (AggregateException exception)
                {
                    AggregateException flattened = exception.Flatten();
                    if (!(flattened.InnerExceptions.Count == 1 &&
                          flattened.InnerExceptions[0] is OperationCanceledException) &&
                        logger != null)
                    {
                        logger.Write("Worker stop error: " + flattened.GetBaseException().Message);
                    }
                }
            }

            if (logger != null)
            {
                logger.Write("Service stopped.");
            }

            if (localCancellation != null &&
                (localWorker == null || localWorker.IsCompleted))
            {
                localCancellation.Dispose();
            }

            cancellation = null;
            workerTask = null;
        }
    }

    internal sealed class LabConfiguration
    {
        private LabConfiguration()
        {
        }

        internal Uri ConnectivityUri { get; private set; }
        internal IPAddress TcpAddress { get; private set; }
        internal int TcpPort { get; private set; }
        internal string SharedToken { get; private set; }
        internal int ConnectTimeoutMilliseconds { get; private set; }
        internal int ReconnectDelayMilliseconds { get; private set; }
        internal string LogPath { get; private set; }
        internal string CommandRoot { get; private set; }

        internal static LabConfiguration FromAppSettings()
        {
            string token = ReadSetting("SharedToken");
            if (!TokenHandshake.IsValidSharedToken(token) ||
                token.IndexOf("CHANGE_ME", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new ConfigurationErrorsException(
                    "SharedToken must be changed from the placeholder and contain 16 to 256 characters.");
            }

            string commandRoot = ReadSetting("CommandRoot");
            if (string.IsNullOrWhiteSpace(commandRoot))
            {
                commandRoot = AppDomain.CurrentDomain.BaseDirectory;
            }

            string logPath = ReadSetting("LogPath");
            if (string.IsNullOrWhiteSpace(logPath))
            {
                logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    InstalledPathName(),
                    "service.log");
            }

            return Create(
                ReadSetting("ConnectivityCheckUrl"),
                ReadSetting("TcpHost"),
                ReadInteger("TcpPort", 1, 65535),
                token,
                ReadInteger("ConnectTimeoutMilliseconds", 250, 60000),
                ReadInteger("ReconnectDelayMilliseconds", 250, 300000),
                logPath,
                commandRoot);
        }

        internal static LabConfiguration ForSelfTest(
            int httpPort,
            int tcpPort,
            string token,
            string logPath,
            string commandRoot)
        {
            return Create(
                "http://127.0.0.1:" + httpPort.ToString(CultureInfo.InvariantCulture) + "/health",
                "127.0.0.1",
                tcpPort,
                token,
                5000,
                250,
                logPath,
                commandRoot);
        }

        private static LabConfiguration Create(
            string connectivityUrl,
            string tcpHost,
            int tcpPort,
            string token,
            int connectTimeoutMilliseconds,
            int reconnectDelayMilliseconds,
            string logPath,
            string commandRoot)
        {
            Uri connectivityUri;
            if (!Uri.TryCreate(connectivityUrl, UriKind.Absolute, out connectivityUri) ||
                !(connectivityUri.Scheme == Uri.UriSchemeHttp ||
                  connectivityUri.Scheme == Uri.UriSchemeHttps))
            {
                throw new ConfigurationErrorsException(
                    "ConnectivityCheckUrl must be an absolute HTTP or HTTPS URL.");
            }

            IPAddress tcpAddress = PrivateNetworkPolicy.ResolveAllowedAddress(tcpHost, "TcpHost");

            string fullCommandRoot = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(commandRoot));
            if (!Directory.Exists(fullCommandRoot))
            {
                throw new ConfigurationErrorsException(
                    "CommandRoot does not exist: " + fullCommandRoot);
            }

            string expandedLogPath = Environment.ExpandEnvironmentVariables(logPath);
            if (!Path.IsPathRooted(expandedLogPath))
            {
                expandedLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, expandedLogPath);
            }

            return new LabConfiguration
            {
                ConnectivityUri = connectivityUri,
                TcpAddress = tcpAddress,
                TcpPort = tcpPort,
                SharedToken = token,
                ConnectTimeoutMilliseconds = connectTimeoutMilliseconds,
                ReconnectDelayMilliseconds = reconnectDelayMilliseconds,
                LogPath = Path.GetFullPath(expandedLogPath),
                CommandRoot = fullCommandRoot
            };
        }

        private static string ReadSetting(string name)
        {
            return ConfigurationManager.AppSettings[name] ?? string.Empty;
        }

        private static int ReadInteger(string name, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse(
                    ReadSetting(name),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value) ||
                value < minimum ||
                value > maximum)
            {
                throw new ConfigurationErrorsException(
                    name + " must be between " + minimum + " and " + maximum + ".");
            }

            return value;
        }

        private static string InstalledPathName()
        {
            return ReverseShellService.InstalledServiceName;
        }
    }

    internal static class PrivateNetworkPolicy
    {
        internal static IPAddress ResolveAllowedAddress(string host, string settingName)
        {
            IPAddress address;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                address = IPAddress.Loopback;
            }
            else if (!IPAddress.TryParse(host, out address))
            {
                throw new ConfigurationErrorsException(
                    settingName + " must use localhost or a literal private IP address; DNS names are not accepted.");
            }

            if (!IsAllowed(address))
            {
                throw new ConfigurationErrorsException(
                    settingName + " must be loopback, RFC1918, link-local, or IPv6 private/link-local.");
            }

            return address;
        }

        internal static bool IsAllowed(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                return bytes[0] == 10 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254);
            }

            if (bytes.Length == 16)
            {
                bool uniqueLocal = (bytes[0] & 0xFE) == 0xFC;
                bool linkLocal = bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
                return uniqueLocal || linkLocal;
            }

            return false;
        }
    }

    internal sealed class LabLogger
    {
        private readonly object syncRoot = new object();
        private readonly string path;

        internal LabLogger(string path)
        {
            this.path = path;
        }

        internal string PathName
        {
            get { return path; }
        }

        internal void Write(string message)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string sanitized = (message ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " ");
                string line = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                              " " + sanitized + Environment.NewLine;

                lock (syncRoot)
                {
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch (Exception exception)
            {
                // Logging is best-effort: a read-only/full disk must not kill the service worker.
                try
                {
                    Console.Error.WriteLine(
                        "NT230.R12.ANTT_Lab2.ReverseShell log failure: " +
                        exception.GetType().Name);
                }
                catch
                {
                    // A Windows service normally has no console; no further fallback is assumed.
                }
            }
        }
    }

    internal sealed class ReverseConnectionWorker
    {
        private readonly LabConfiguration configuration;
        private readonly LabLogger logger;
        private readonly ManagedCommandDispatcher dispatcher;

        internal ReverseConnectionWorker(LabConfiguration configuration, LabLogger logger)
        {
            this.configuration = configuration;
            this.logger = logger;
            dispatcher = new ManagedCommandDispatcher(configuration.CommandRoot);
        }

        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            logger.Write("Worker loop started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunSingleSessionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    logger.Write("Session timed out.");
                }
                catch (Exception exception)
                {
                    logger.Write(
                        "Session failed (" + exception.GetType().Name + "): " + exception.Message);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    logger.Write(
                        "Reconnect scheduled in " +
                        configuration.ReconnectDelayMilliseconds.ToString(CultureInfo.InvariantCulture) +
                        " ms.");
                    try
                    {
                        await Task.Delay(
                            configuration.ReconnectDelayMilliseconds,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            logger.Write("Worker loop stopped.");
        }

        internal async Task RunSingleSessionAsync(CancellationToken cancellationToken)
        {
            await CheckConnectivityAsync(cancellationToken).ConfigureAwait(false);

            using (TcpClient client = new TcpClient(configuration.TcpAddress.AddressFamily))
            {
                await ConnectWithTimeoutAsync(client, cancellationToken).ConfigureAwait(false);
                logger.Write(
                    "TCP connected to private endpoint " +
                    configuration.TcpAddress + ":" +
                    configuration.TcpPort.ToString(CultureInfo.InvariantCulture) + ".");

                using (CancellationTokenRegistration cancellationRegistration =
                    cancellationToken.Register(client.Close))
                using (NetworkStream stream = client.GetStream())
                {
                    string challengeLine = await LineProtocol.ReadLineAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                    byte[] serverNonce = TokenHandshake.ParseChallenge(challengeLine);
                    byte[] clientNonce = TokenHandshake.CreateNonce();
                    await LineProtocol.WriteLineAsync(
                        stream,
                        "AUTH " +
                        Convert.ToBase64String(clientNonce) + " " +
                        TokenHandshake.CreateClientProof(
                            configuration.SharedToken,
                            serverNonce,
                            clientNonce),
                        cancellationToken).ConfigureAwait(false);

                    string authenticationReply = await LineProtocol.ReadLineAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                    const string authenticationPrefix = "OK ";
                    string expectedServerProof = TokenHandshake.CreateServerProof(
                        configuration.SharedToken,
                        serverNonce,
                        clientNonce);
                    if (authenticationReply == null ||
                        !authenticationReply.StartsWith(
                            authenticationPrefix,
                            StringComparison.Ordinal) ||
                        !TokenHandshake.FixedTimeEquals(
                            authenticationReply.Substring(authenticationPrefix.Length),
                            expectedServerProof))
                    {
                        throw new InvalidDataException(
                            "Controller mutual authentication failed.");
                    }

                    logger.Write("Mutual authentication accepted.");
                    await LineProtocol.WriteLineAsync(
                        stream,
                        "READY",
                        cancellationToken).ConfigureAwait(false);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        string command = await LineProtocol.ReadLineAsync(
                            stream,
                            cancellationToken).ConfigureAwait(false);
                        if (command == null)
                        {
                            logger.Write("Peer closed the session.");
                            return;
                        }

                        CommandResult result = dispatcher.Execute(command);
                        string prefix = result.CloseSession
                            ? "BYE "
                            : (result.IsError ? "ERR " : "OK ");

                        await LineProtocol.WriteLineAsync(
                            stream,
                            prefix + LineProtocol.Encode(result.Output),
                            cancellationToken).ConfigureAwait(false);

                        if (result.CloseSession)
                        {
                            logger.Write("Session ended by the allowlisted exit command.");
                            return;
                        }
                    }
                }
            }
        }

        private async Task CheckConnectivityAsync(CancellationToken cancellationToken)
        {
            using (HttpClientHandler handler = new HttpClientHandler())
            {
                handler.UseProxy = false;
                handler.AllowAutoRedirect = false;
                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMilliseconds(
                        configuration.ConnectTimeoutMilliseconds);
                    using (HttpResponseMessage response = await client.GetAsync(
                        configuration.ConnectivityUri,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException(
                                "Connectivity check returned HTTP " +
                                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                        }
                    }
                }
            }

            logger.Write("HTTP connectivity check succeeded.");
        }

        private async Task ConnectWithTimeoutAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            Task connectTask = client.ConnectAsync(
                configuration.TcpAddress,
                configuration.TcpPort);
            Task timeoutTask = Task.Delay(
                configuration.ConnectTimeoutMilliseconds,
                cancellationToken);

            Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (completed != connectTask)
            {
                client.Close();
                ObserveLateFault(connectTask);
                cancellationToken.ThrowIfCancellationRequested();
                throw new System.TimeoutException("TCP connection timed out.");
            }

            await connectTask.ConfigureAwait(false);
        }

        private static void ObserveLateFault(Task task)
        {
            task.ContinueWith(
                completed => completed.Exception.Handle(inner => true),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal sealed class ManagedCommandDispatcher
    {
        private const int MaximumDirectoryEntries = 20;
        private const int MaximumCommandBytes = 4096;
        private readonly string commandRoot;

        internal ManagedCommandDispatcher(string commandRoot)
        {
            this.commandRoot = commandRoot;
        }

        internal CommandResult Execute(string commandLine)
        {
            string input = (commandLine ?? string.Empty).Trim();
            if (Encoding.UTF8.GetByteCount(input) > MaximumCommandBytes)
            {
                return CommandResult.Error("Command exceeded the 4096-byte limit.");
            }

            if (input.Length == 0)
            {
                return CommandResult.Error("Empty command. Use help to list allowed commands.");
            }

            int separator = input.IndexOf(' ');
            string command = separator < 0 ? input : input.Substring(0, separator);
            string argument = separator < 0 ? string.Empty : input.Substring(separator + 1);

            switch (command.ToLowerInvariant())
            {
                case "help":
                    return RequireNoArgument(
                        argument,
                        "help",
                        "Allowed managed commands: help, hostname, whoami, pwd, dir, " +
                        "echo <text>, time, exit. External processes, cmd.exe, and PowerShell are not used.");

                case "hostname":
                    return RequireNoArgument(argument, "hostname", Environment.MachineName);

                case "whoami":
                    if (!string.IsNullOrWhiteSpace(argument))
                    {
                        return CommandResult.Error("whoami does not accept arguments.");
                    }

                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        return CommandResult.Success(identity.Name);
                    }

                case "pwd":
                    return RequireNoArgument(argument, "pwd", commandRoot);

                case "dir":
                    if (!string.IsNullOrWhiteSpace(argument))
                    {
                        return CommandResult.Error(
                            "dir does not accept a path; it only lists the configured CommandRoot.");
                    }

                    return ListCommandRoot();

                case "echo":
                    return CommandResult.Success(argument);

                case "time":
                    return RequireNoArgument(
                        argument,
                        "time",
                        DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));

                case "exit":
                    if (!string.IsNullOrWhiteSpace(argument))
                    {
                        return CommandResult.Error("exit does not accept arguments.");
                    }

                    return CommandResult.Exit("Session closed.");

                default:
                    return CommandResult.Error(
                        "Command not allowed. Use help to list the managed allowlist.");
            }
        }

        private CommandResult ListCommandRoot()
        {
            try
            {
                List<FileSystemInfo> entries = new DirectoryInfo(commandRoot)
                    .EnumerateFileSystemInfos()
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumDirectoryEntries + 1)
                    .ToList();

                bool truncated = entries.Count > MaximumDirectoryEntries;
                IEnumerable<string> lines = entries
                    .Take(MaximumDirectoryEntries)
                    .Select(entry =>
                        (entry is DirectoryInfo ? "[D] " : "[F] ") + entry.Name);

                string output = string.Join(Environment.NewLine, lines);
                if (truncated)
                {
                    output += Environment.NewLine +
                              "[truncated after " +
                              MaximumDirectoryEntries.ToString(CultureInfo.InvariantCulture) +
                              " entries]";
                }

                return CommandResult.Success(
                    string.IsNullOrEmpty(output) ? "[empty directory]" : output);
            }
            catch (Exception exception)
            {
                return CommandResult.Error(
                    "Unable to list CommandRoot: " + exception.GetType().Name + ".");
            }
        }

        private static CommandResult RequireNoArgument(
            string argument,
            string command,
            string output)
        {
            return string.IsNullOrWhiteSpace(argument)
                ? CommandResult.Success(output)
                : CommandResult.Error(command + " does not accept arguments.");
        }
    }

    internal sealed class CommandResult
    {
        private CommandResult(string output, bool isError, bool closeSession)
        {
            Output = output ?? string.Empty;
            IsError = isError;
            CloseSession = closeSession;
        }

        internal string Output { get; private set; }
        internal bool IsError { get; private set; }
        internal bool CloseSession { get; private set; }

        internal static CommandResult Success(string output)
        {
            return new CommandResult(output, false, false);
        }

        internal static CommandResult Error(string output)
        {
            return new CommandResult(output, true, false);
        }

        internal static CommandResult Exit(string output)
        {
            return new CommandResult(output, false, true);
        }
    }

    internal static class LineProtocol
    {
        private const int MaximumLineBytes = 65536;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static async Task<string> ReadLineAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] oneByte = new byte[1];
                while (true)
                {
                    int count = await stream.ReadAsync(
                        oneByte,
                        0,
                        1,
                        cancellationToken).ConfigureAwait(false);

                    if (count == 0)
                    {
                        return buffer.Length == 0
                            ? null
                            : StrictUtf8.GetString(buffer.ToArray());
                    }

                    if (oneByte[0] == (byte)'\n')
                    {
                        return StrictUtf8.GetString(buffer.ToArray());
                    }

                    if (oneByte[0] != (byte)'\r')
                    {
                        if (buffer.Length >= MaximumLineBytes)
                        {
                            throw new InvalidDataException(
                                "Protocol line exceeded 65536 bytes.");
                        }

                        buffer.WriteByte(oneByte[0]);
                    }
                }
            }
        }

        internal static async Task WriteLineAsync(
            Stream stream,
            string line,
            CancellationToken cancellationToken)
        {
            byte[] bytes = StrictUtf8.GetBytes((line ?? string.Empty) + "\n");
            if (bytes.Length > MaximumLineBytes + 1)
            {
                throw new InvalidDataException("Protocol line exceeded 65536 bytes.");
            }

            await stream.WriteAsync(
                bytes,
                0,
                bytes.Length,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static string Encode(string value)
        {
            return Convert.ToBase64String(StrictUtf8.GetBytes(value ?? string.Empty));
        }

        internal static string Decode(string value)
        {
            return StrictUtf8.GetString(Convert.FromBase64String(value ?? string.Empty));
        }
    }

    internal static class TokenHandshake
    {
        internal static byte[] CreateNonce()
        {
            byte[] nonce = new byte[32];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(nonce);
            }

            return nonce;
        }

        internal static string CreateClientProof(
            string token,
            byte[] serverNonce,
            byte[] clientNonce)
        {
            return CreateDomainProof(token, "client", serverNonce, clientNonce);
        }

        internal static string CreateServerProof(
            string token,
            byte[] serverNonce,
            byte[] clientNonce)
        {
            return CreateDomainProof(token, "server", serverNonce, clientNonce);
        }

        internal static bool IsValidSharedToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token) &&
                   token.Length >= 16 &&
                   token.Length <= 256;
        }

        internal static byte[] ParseChallenge(string line)
        {
            const string prefix = "CHALLENGE ";
            if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The peer did not send an authentication challenge.");
            }

            return ParseNonce(line.Substring(prefix.Length), "server challenge");
        }

        internal static byte[] ParseNonce(string encoded, string label)
        {
            byte[] nonce;
            try
            {
                nonce = Convert.FromBase64String(encoded ?? string.Empty);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "The " + label + " was not valid base64.",
                    exception);
            }

            if (nonce.Length < 16 || nonce.Length > 64)
            {
                throw new InvalidDataException(
                    "The " + label + " must contain 16 to 64 bytes.");
            }

            return nonce;
        }

        internal static bool FixedTimeEquals(string left, string right)
        {
            byte[] leftBytes;
            byte[] rightBytes;
            try
            {
                leftBytes = Convert.FromBase64String(left ?? string.Empty);
                rightBytes = Convert.FromBase64String(right ?? string.Empty);
            }
            catch (FormatException)
            {
                return false;
            }

            int difference = leftBytes.Length ^ rightBytes.Length;
            int maximum = Math.Max(leftBytes.Length, rightBytes.Length);
            for (int index = 0; index < maximum; index++)
            {
                byte leftByte = index < leftBytes.Length ? leftBytes[index] : (byte)0;
                byte rightByte = index < rightBytes.Length ? rightBytes[index] : (byte)0;
                difference |= leftByte ^ rightByte;
            }

            return difference == 0;
        }

        private static string CreateDomainProof(
            string token,
            string role,
            byte[] serverNonce,
            byte[] clientNonce)
        {
            if (!IsValidSharedToken(token))
            {
                throw new ArgumentException(
                    "Shared token must contain 16 to 256 characters.",
                    "token");
            }

            ValidateNonce(serverNonce, "server nonce");
            ValidateNonce(clientNonce, "client nonce");

            byte[] key = Encoding.UTF8.GetBytes(token);
            byte[] domain = Encoding.ASCII.GetBytes(
                "NT230.R12.ANTT_Lab2.ReverseShell/" + role + "/v1");
            byte[] payload;
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(domain, 0, domain.Length);
                stream.WriteByte(0);
                stream.WriteByte((byte)serverNonce.Length);
                stream.Write(serverNonce, 0, serverNonce.Length);
                stream.WriteByte((byte)clientNonce.Length);
                stream.Write(clientNonce, 0, clientNonce.Length);
                payload = stream.ToArray();
            }

            try
            {
                using (HMACSHA256 hmac = new HMACSHA256(key))
                {
                    return Convert.ToBase64String(hmac.ComputeHash(payload));
                }
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
                Array.Clear(payload, 0, payload.Length);
            }
        }

        private static void ValidateNonce(byte[] nonce, string label)
        {
            if (nonce == null || nonce.Length < 16 || nonce.Length > 64)
            {
                throw new ArgumentException(label + " must contain 16 to 64 bytes.");
            }
        }
    }

    internal static class ListenerConsole
    {
        internal static async Task<int> RunAsync(
            string bindHost,
            string portText,
            string sharedToken)
        {
            int port;
            if (!int.TryParse(
                    portText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out port) ||
                port < 1 ||
                port > 65535)
            {
                throw new ArgumentException("Listener port must be between 1 and 65535.");
            }

            if (!TokenHandshake.IsValidSharedToken(sharedToken))
            {
                throw new ArgumentException(
                    "Shared token must contain 16 to 256 characters.");
            }

            IPAddress bindAddress = PrivateNetworkPolicy.ResolveAllowedAddress(
                bindHost,
                "bindIp");
            TcpListener listener = new TcpListener(bindAddress, port);
            listener.Start();

            try
            {
                Console.WriteLine(
                    "LISTENER READY " + bindAddress + ":" +
                    port.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Waiting for one service connection...");

                using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                    if (remote == null || !PrivateNetworkPolicy.IsAllowed(remote.Address))
                    {
                        throw new InvalidDataException(
                            "Rejected a connection whose source address is not private.");
                    }

                    Console.WriteLine("CONNECTED " + remote.Address + ":" + remote.Port);
                    using (NetworkStream stream = client.GetStream())
                    {
                        await ListenerProtocol.AuthenticateServiceAsync(
                            stream,
                            sharedToken,
                            CancellationToken.None).ConfigureAwait(false);
                        Console.WriteLine("MUTUAL AUTH OK");

                        while (true)
                        {
                            Console.Write("lab> ");
                            string command = Console.ReadLine();
                            if (command == null)
                            {
                                command = "exit";
                                Console.WriteLine("exit");
                            }

                            string validationError;
                            if (!ListenerCommandPolicy.TryValidate(
                                    command,
                                    out validationError))
                            {
                                Console.WriteLine("LOCAL ERR: " + validationError);
                                continue;
                            }

                            ListenerReply reply = await ListenerProtocol.SendCommandAsync(
                                stream,
                                command,
                                CancellationToken.None).ConfigureAwait(false);
                            Console.WriteLine(reply.Prefix + ": " + reply.Output);
                            if (string.Equals(
                                    reply.Prefix,
                                    "BYE",
                                    StringComparison.Ordinal))
                            {
                                Console.WriteLine("LISTENER SESSION CLOSED");
                                return 0;
                            }
                        }
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    internal static class ListenerCommandPolicy
    {
        private const int MaximumCommandBytes = 4096;
        private static readonly HashSet<string> AllowedCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "help",
                "hostname",
                "whoami",
                "pwd",
                "dir",
                "echo",
                "time",
                "exit"
            };

        internal static bool TryValidate(string commandLine, out string error)
        {
            string input = (commandLine ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                error = "Command is empty.";
                return false;
            }

            if (Encoding.UTF8.GetByteCount(input) > MaximumCommandBytes)
            {
                error = "Command exceeded the 4096-byte limit.";
                return false;
            }

            int separator = input.IndexOf(' ');
            string command = separator < 0 ? input : input.Substring(0, separator);
            if (!AllowedCommands.Contains(command))
            {
                error =
                    "Command not allowed. Use: help, hostname, whoami, pwd, dir, " +
                    "echo <text>, time, exit.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }

    internal static class ListenerProtocol
    {
        internal static async Task AuthenticateServiceAsync(
            Stream stream,
            string sharedToken,
            CancellationToken cancellationToken)
        {
            byte[] serverNonce = TokenHandshake.CreateNonce();
            await LineProtocol.WriteLineAsync(
                stream,
                "CHALLENGE " + Convert.ToBase64String(serverNonce),
                cancellationToken).ConfigureAwait(false);

            string authentication = await LineProtocol.ReadLineAsync(
                stream,
                cancellationToken).ConfigureAwait(false);

            try
            {
                string[] parts = (authentication ?? string.Empty).Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 ||
                    !string.Equals(parts[0], "AUTH", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Malformed service authentication reply.");
                }

                byte[] clientNonce = TokenHandshake.ParseNonce(
                    parts[1],
                    "client nonce");
                string expectedClientProof = TokenHandshake.CreateClientProof(
                    sharedToken,
                    serverNonce,
                    clientNonce);
                if (!TokenHandshake.FixedTimeEquals(parts[2], expectedClientProof))
                {
                    throw new InvalidDataException("Service token proof did not match.");
                }

                string serverProof = TokenHandshake.CreateServerProof(
                    sharedToken,
                    serverNonce,
                    clientNonce);
                await LineProtocol.WriteLineAsync(
                    stream,
                    "OK " + serverProof,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                try
                {
                    await LineProtocol.WriteLineAsync(
                        stream,
                        "DENIED",
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the authentication failure as the primary error.
                }

                throw;
            }

            string ready = await LineProtocol.ReadLineAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ready, "READY", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Service did not confirm controller authentication.");
            }
        }

        internal static async Task<ListenerReply> SendCommandAsync(
            Stream stream,
            string command,
            CancellationToken cancellationToken)
        {
            await LineProtocol.WriteLineAsync(
                stream,
                command,
                cancellationToken).ConfigureAwait(false);
            string line = await LineProtocol.ReadLineAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                throw new EndOfStreamException(
                    "Service closed before returning a command reply.");
            }

            int separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                throw new InvalidDataException("Malformed command reply.");
            }

            string prefix = line.Substring(0, separator);
            if (!(prefix == "OK" || prefix == "ERR" || prefix == "BYE"))
            {
                throw new InvalidDataException("Unknown command-reply status.");
            }

            string output;
            try
            {
                output = LineProtocol.Decode(line.Substring(separator + 1));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Command reply was not valid base64.",
                    exception);
            }

            return new ListenerReply(prefix, output);
        }
    }

    internal sealed class ListenerReply
    {
        internal ListenerReply(string prefix, string output)
        {
            Prefix = prefix;
            Output = output;
        }

        internal string Prefix { get; private set; }
        internal string Output { get; private set; }
    }

    [RunInstaller(true)]
    public sealed class ProjectInstaller : Installer
    {
        public ProjectInstaller()
        {
            ServiceProcessInstaller processInstaller = new ServiceProcessInstaller
            {
                Account = ServiceAccount.LocalSystem
            };

            ServiceInstaller serviceInstaller = new ServiceInstaller
            {
                ServiceName = ReverseShellService.InstalledServiceName,
                DisplayName = "NT230.R12.ANTT_Lab2.ReverseShell",
                Description = "Educational Windows Service lab source archive.",
                StartType = ServiceStartMode.Manual
            };

            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }

    internal static class SelfTestRunner
    {
        internal static async Task<int> RunAsync()
        {
            string listenerPolicyError;
            Assert(
                ListenerCommandPolicy.TryValidate("help", out listenerPolicyError),
                "Listener rejected an allowlisted command.");
            Assert(
                !ListenerCommandPolicy.TryValidate(
                    "cmd /c whoami",
                    out listenerPolicyError),
                "Listener accepted a non-allowlisted command.");
            Assert(PrivateNetworkPolicy.IsAllowed(IPAddress.Loopback), "Loopback was rejected.");
            Assert(
                PrivateNetworkPolicy.IsAllowed(IPAddress.Parse("10.20.30.40")),
                "RFC1918 address was rejected.");
            Assert(
                !PrivateNetworkPolicy.IsAllowed(IPAddress.Parse("8.8.8.8")),
                "Public endpoint was accepted.");

            // Keep the self-test path short so the .NET Framework build remains
            // usable from deeply nested clone locations that still enforce MAX_PATH.
            string artifactRoot = Path.Combine(
                Path.GetTempPath(),
                "NT230-Lab2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string commandRoot = Path.Combine(artifactRoot, "command-root");
            string logPath = Path.Combine(artifactRoot, "service.log");
            Directory.CreateDirectory(commandRoot);
            File.WriteAllText(
                Path.Combine(commandRoot, "selftest-fixture.txt"),
                "managed-command fixture",
                new UTF8Encoding(false));

            TcpListener httpListener = new TcpListener(IPAddress.Loopback, 0);
            TcpListener commandListener = new TcpListener(IPAddress.Loopback, 0);
            httpListener.Start();
            commandListener.Start();

            int httpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;
            int commandPort = ((IPEndPoint)commandListener.LocalEndpoint).Port;
            string token = "self-test-token-" + Guid.NewGuid().ToString("N");

            LabConfiguration configuration = LabConfiguration.ForSelfTest(
                httpPort,
                commandPort,
                token,
                logPath,
                commandRoot);
            LabLogger logger = new LabLogger(logPath);
            ReverseConnectionWorker worker = new ReverseConnectionWorker(configuration, logger);

            Console.WriteLine("SELF-TEST START");
            Console.WriteLine("HTTP endpoint: 127.0.0.1:" + httpPort);
            Console.WriteLine("TCP endpoint: 127.0.0.1:" + commandPort);

            using (CancellationTokenSource timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(20)))
            using (CancellationTokenSource workerCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                try
                {
                    Task httpTask = ServeHealthCheckAsync(httpListener, timeout.Token);
                    Task commandTask = ServeCommandSessionAsync(
                        commandListener,
                        token,
                        commandRoot,
                        timeout.Token);
                    Task workerTask = worker.RunAsync(workerCancellation.Token);

                    await Task.WhenAll(httpTask, commandTask).ConfigureAwait(false);
                    await WaitForLogEntryAsync(
                        logPath,
                        "Reconnect scheduled",
                        timeout.Token).ConfigureAwait(false);
                    workerCancellation.Cancel();
                    await workerTask.ConfigureAwait(false);

                    string log = File.ReadAllText(logPath);
                    AssertContains(log, "HTTP connectivity check succeeded.", "HTTP log");
                    AssertContains(log, "Mutual authentication accepted.", "authentication log");
                    AssertContains(log, "allowlisted exit command", "exit log");
                    AssertContains(log, "Reconnect scheduled", "reconnect log");
                    AssertContains(log, "Worker loop stopped", "cancellation log");

                    await VerifyControllerWithoutTokenRejectedAsync(
                        artifactRoot,
                        token,
                        commandRoot,
                        timeout.Token).ConfigureAwait(false);

                    Console.WriteLine("PASS HTTP connectivity check");
                    Console.WriteLine("PASS TCP private-endpoint policy (loopback/RFC1918 allowed, public rejected)");
                    Console.WriteLine("PASS mutual nonce-HMAC shared-token handshake");
                    Console.WriteLine("PASS controller without token rejected before READY");
                    Console.WriteLine("PASS interactive listener allowlist policy");
                    Console.WriteLine("PASS managed allowlist: help/hostname/whoami/pwd/dir/echo/time/exit");
                    Console.WriteLine("PASS blocked external command");
                    Console.WriteLine("PASS reconnect scheduling, cancellation, and log assertions");
                    Console.WriteLine("SELF-TEST ARTIFACTS: " + artifactRoot);
                    Console.WriteLine("SELF-TEST PASS");
                    return 0;
                }
                finally
                {
                    workerCancellation.Cancel();
                    httpListener.Stop();
                    commandListener.Stop();
                }
            }
        }

        private static async Task VerifyControllerWithoutTokenRejectedAsync(
            string artifactRoot,
            string token,
            string commandRoot,
            CancellationToken cancellationToken)
        {
            TcpListener httpListener = new TcpListener(IPAddress.Loopback, 0);
            TcpListener fakeController = new TcpListener(IPAddress.Loopback, 0);
            httpListener.Start();
            fakeController.Start();

            try
            {
                int httpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;
                int tcpPort = ((IPEndPoint)fakeController.LocalEndpoint).Port;
                string negativeLog = Path.Combine(
                    artifactRoot,
                    "controller-rejection.log");
                LabConfiguration configuration = LabConfiguration.ForSelfTest(
                    httpPort,
                    tcpPort,
                    token,
                    negativeLog,
                    commandRoot);
                ReverseConnectionWorker worker = new ReverseConnectionWorker(
                    configuration,
                    new LabLogger(negativeLog));

                Task httpTask = ServeHealthCheckAsync(
                    httpListener,
                    cancellationToken);
                Task controllerTask = ServeFakeControllerWithoutTokenAsync(
                    fakeController,
                    cancellationToken);

                bool rejected = false;
                try
                {
                    await worker.RunSingleSessionAsync(
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    rejected = exception.Message.IndexOf(
                        "mutual authentication",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }

                await Task.WhenAll(httpTask, controllerTask).ConfigureAwait(false);
                Assert(
                    rejected,
                    "Service accepted a controller that did not prove knowledge of the token.");
            }
            finally
            {
                httpListener.Stop();
                fakeController.Stop();
            }
        }

        private static async Task ServeFakeControllerWithoutTokenAsync(
            TcpListener listener,
            CancellationToken cancellationToken)
        {
            using (TcpClient client = await AcceptClientAsync(
                listener,
                cancellationToken).ConfigureAwait(false))
            using (NetworkStream stream = client.GetStream())
            {
                byte[] serverNonce = TokenHandshake.CreateNonce();
                await LineProtocol.WriteLineAsync(
                    stream,
                    "CHALLENGE " + Convert.ToBase64String(serverNonce),
                    cancellationToken).ConfigureAwait(false);

                string clientAuthentication = await LineProtocol.ReadLineAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false);
                Assert(
                    clientAuthentication != null &&
                    clientAuthentication.StartsWith("AUTH ", StringComparison.Ordinal),
                    "Service did not return its client proof.");

                byte[] invalidProof = new byte[32];
                await LineProtocol.WriteLineAsync(
                    stream,
                    "OK " + Convert.ToBase64String(invalidProof),
                    cancellationToken).ConfigureAwait(false);

                string unexpectedReady = await LineProtocol.ReadLineAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false);
                Assert(
                    unexpectedReady == null,
                    "Service sent READY without validating the controller proof.");
            }
        }

        private static async Task WaitForLogEntryAsync(
            string logPath,
            string expected,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (File.Exists(logPath) &&
                    File.ReadAllText(logPath).IndexOf(
                        expected,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                "Timed out waiting for log entry '" + expected + "'.");
        }

        private static async Task ServeHealthCheckAsync(
            TcpListener listener,
            CancellationToken cancellationToken)
        {
            using (TcpClient client = await AcceptClientAsync(
                listener,
                cancellationToken).ConfigureAwait(false))
            using (NetworkStream stream = client.GetStream())
            {
                string requestLine = await LineProtocol.ReadLineAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false);
                Assert(
                    requestLine != null &&
                    requestLine.StartsWith("GET /health ", StringComparison.Ordinal),
                    "HTTP connectivity check did not request /health.");

                string header;
                do
                {
                    header = await LineProtocol.ReadLineAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                }
                while (header != null && header.Length != 0);

                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/plain\r\n" +
                    "Content-Length: 2\r\n" +
                    "Connection: close\r\n\r\nOK");
                await stream.WriteAsync(
                    response,
                    0,
                    response.Length,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task ServeCommandSessionAsync(
            TcpListener listener,
            string token,
            string commandRoot,
            CancellationToken cancellationToken)
        {
            using (TcpClient client = await AcceptClientAsync(
                listener,
                cancellationToken).ConfigureAwait(false))
            using (NetworkStream stream = client.GetStream())
            {
                await ListenerProtocol.AuthenticateServiceAsync(
                    stream,
                    token,
                    cancellationToken).ConfigureAwait(false);

                ProtocolReply help = await SendCommandAsync(
                    stream, "help", cancellationToken).ConfigureAwait(false);
                Assert(help.Prefix == "OK", "help failed.");
                AssertContains(help.Output, "hostname", "help output");
                AssertContains(help.Output, "cmd.exe", "help containment note");

                ProtocolReply hostname = await SendCommandAsync(
                    stream, "hostname", cancellationToken).ConfigureAwait(false);
                Assert(hostname.Prefix == "OK" && hostname.Output.Length > 0, "hostname failed.");

                ProtocolReply whoami = await SendCommandAsync(
                    stream, "whoami", cancellationToken).ConfigureAwait(false);
                Assert(whoami.Prefix == "OK" && whoami.Output.Length > 0, "whoami failed.");

                ProtocolReply pwd = await SendCommandAsync(
                    stream, "pwd", cancellationToken).ConfigureAwait(false);
                Assert(
                    pwd.Prefix == "OK" &&
                    string.Equals(
                        Path.GetFullPath(pwd.Output).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(commandRoot).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase),
                    "pwd escaped CommandRoot.");

                ProtocolReply directory = await SendCommandAsync(
                    stream, "dir", cancellationToken).ConfigureAwait(false);
                Assert(directory.Prefix == "OK", "dir failed.");
                AssertContains(directory.Output, "selftest-fixture.txt", "dir output");

                ProtocolReply echo = await SendCommandAsync(
                    stream, "echo self-test echo", cancellationToken).ConfigureAwait(false);
                Assert(
                    echo.Prefix == "OK" &&
                    string.Equals(echo.Output, "self-test echo", StringComparison.Ordinal),
                    "echo failed.");

                ProtocolReply time = await SendCommandAsync(
                    stream, "time", cancellationToken).ConfigureAwait(false);
                DateTimeOffset parsedTime;
                Assert(
                    time.Prefix == "OK" &&
                    DateTimeOffset.TryParse(
                        time.Output,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out parsedTime),
                    "time failed.");

                ProtocolReply blocked = await SendCommandAsync(
                    stream, "cmd /c whoami", cancellationToken).ConfigureAwait(false);
                Assert(blocked.Prefix == "ERR", "External command was not blocked.");
                AssertContains(blocked.Output, "not allowed", "blocked-command reply");

                ProtocolReply oversized = await SendCommandAsync(
                    stream,
                    "echo " + new string('A', 4100),
                    cancellationToken).ConfigureAwait(false);
                Assert(oversized.Prefix == "ERR", "Oversized command was not blocked.");
                AssertContains(oversized.Output, "4096-byte", "oversized-command reply");

                ProtocolReply exit = await SendCommandAsync(
                    stream, "exit", cancellationToken).ConfigureAwait(false);
                Assert(exit.Prefix == "BYE", "exit failed.");

                string afterExit = await LineProtocol.ReadLineAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false);
                Assert(afterExit == null, "Connection remained open after exit.");
            }
        }

        private static async Task<ProtocolReply> SendCommandAsync(
            Stream stream,
            string command,
            CancellationToken cancellationToken)
        {
            await LineProtocol.WriteLineAsync(
                stream,
                command,
                cancellationToken).ConfigureAwait(false);
            string line = await LineProtocol.ReadLineAsync(
                stream,
                cancellationToken).ConfigureAwait(false);
            Assert(line != null, "Peer closed before replying to " + command + ".");

            int separator = line.IndexOf(' ');
            Assert(separator > 0, "Malformed reply to " + command + ".");
            string prefix = line.Substring(0, separator);
            string encoded = line.Substring(separator + 1);
            string output;
            try
            {
                output = LineProtocol.Decode(encoded);
            }
            catch (FormatException)
            {
                throw new InvalidDataException("Reply to " + command + " was not valid base64.");
            }

            return new ProtocolReply(prefix, output);
        }

        private static async Task<TcpClient> AcceptClientAsync(
            TcpListener listener,
            CancellationToken cancellationToken)
        {
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            Task cancellationTask = Task.Delay(
                Timeout.Infinite,
                cancellationToken);
            Task completed = await Task.WhenAny(
                acceptTask,
                cancellationTask).ConfigureAwait(false);

            if (completed != acceptTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new System.TimeoutException("Listener timed out.");
            }

            return await acceptTask.ConfigureAwait(false);
        }

        private static void AssertContains(string value, string expected, string label)
        {
            Assert(
                value != null &&
                value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0,
                label + " did not contain '" + expected + "'.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class ProtocolReply
        {
            internal ProtocolReply(string prefix, string output)
            {
                Prefix = prefix;
                Output = output;
            }

            internal string Prefix { get; private set; }
            internal string Output { get; private set; }
        }
    }
}
