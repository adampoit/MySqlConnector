﻿using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using MySql.Data.Protocol.Serialization;

namespace MySql.Data.Serialization
{
	internal sealed class MySqlSession
	{
		public MySqlSession()
			: this(null, 0)
		{
		}

		public MySqlSession(ConnectionPool pool, int poolGeneration)
		{
			m_lock = new object();
			CreatedUtc = DateTime.UtcNow;
			Pool = pool;
			PoolGeneration = poolGeneration;
		}

		public ServerVersion ServerVersion { get; set; }
		public int ConnectionId { get; set; }
		public byte[] AuthPluginData { get; set; }
		public DateTime CreatedUtc { get; }
		public ConnectionPool Pool { get; }
		public int PoolGeneration { get; }
		public DateTime LastReturnedUtc { get; private set; }
		public string DatabaseOverride { get; set; }
		public IPAddress IPAddress => (m_tcpClient?.Client.RemoteEndPoint as IPEndPoint)?.Address;

		public void ReturnToPool()
		{
			LastReturnedUtc = DateTime.UtcNow;
			Pool?.Return(this);
		}

		public bool IsConnected
		{
			get
			{
				lock (m_lock)
					return m_state == State.Connected;
			}
		}

		public bool TryStartCancel(MySqlCommand command)
		{
			lock (m_lock)
			{
				if (m_activeCommand != command)
					return false;
				VerifyState(State.Querying, State.CancelingQuery);
				if (m_state != State.Querying)
					return false;
				m_state = State.CancelingQuery;
			}

			return true;
		}

		public void DoCancel(MySqlCommand commandToCancel, MySqlCommand killCommand)
		{
			lock (m_lock)
			{
				if (m_activeCommand != commandToCancel)
					return;

				// NOTE: This command is executed while holding the lock to prevent race conditions during asynchronous cancellation.
				// For example, if the lock weren't held, the current command could finish and the other thread could set m_activeCommand
				// to null, then start executing a new command. By the time this "KILL QUERY" command reached the server, the wrong
				// command would be killed (because "KILL QUERY" specifies the connection whose command should be killed, not
				// a unique identifier of the command itself). As a mitigation, we set the CommandTimeout to a low value to avoid
				// blocking the other thread for an extended duration.
				killCommand.CommandTimeout = 3;
				killCommand.ExecuteNonQuery();
			}
		}

		public void AbortCancel(MySqlCommand command)
		{
			lock (m_lock)
			{
				if (m_activeCommand == command && m_state == State.CancelingQuery)
					m_state = State.Querying;
			}
		}

		public void StartQuerying(MySqlCommand command)
		{
			lock (m_lock)
			{
				if (m_state == State.Querying || m_state == State.CancelingQuery)
					throw new MySqlException("There is already an open DataReader associated with this Connection which must be closed first.");

				VerifyState(State.Connected);
				m_state = State.Querying;
				m_activeCommand = command;
			}
		}

		public MySqlDataReader ActiveReader => m_activeReader;

		public void SetActiveReader(MySqlDataReader dataReader)
		{
			VerifyState(State.Querying, State.CancelingQuery);
			if (dataReader == null)
				throw new ArgumentNullException(nameof(dataReader));
			if (m_activeReader != null)
				throw new InvalidOperationException("Can't replace active reader.");
			m_activeReader = dataReader;
		}

		public void FinishQuerying()
		{
			lock (m_lock)
			{
				VerifyState(State.Querying, State.CancelingQuery);
				m_state = State.Connected;
				m_activeReader = null;
				m_activeCommand = null;
			}
		}

		public async Task DisposeAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			if (m_payloadHandler != null)
			{
				// attempt to gracefully close the connection, ignoring any errors (it may have been closed already by the server, etc.)
				lock (m_lock)
				{
					VerifyState(State.Connected, State.Failed);
					m_state = State.Closing;
				}
				try
				{
					m_payloadHandler.StartNewConversation();
					await m_payloadHandler.WritePayloadAsync(QuitPayload.Create(), ioBehavior).ConfigureAwait(false);
					await m_payloadHandler.ReadPayloadAsync(ProtocolErrorBehavior.Ignore, ioBehavior).ConfigureAwait(false);
				}
				catch (IOException)
				{
				}
				catch (SocketException)
				{
				}
			}
			ShutdownSocket();
			lock (m_lock)
				m_state = State.Closed;
		}

		public async Task ConnectAsync(ConnectionSettings cs, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				VerifyState(State.Created);
				m_state = State.Connecting;
			}
			var connected = false;
			if (cs.ConnectionType == ConnectionType.Tcp)
				connected = await OpenTcpSocketAsync(cs, ioBehavior, cancellationToken).ConfigureAwait(false);
			else if (cs.ConnectionType == ConnectionType.Unix)
				connected = await OpenUnixSocketAsync(cs, ioBehavior, cancellationToken).ConfigureAwait(false);
			if (!connected)
			{
				lock (m_lock)
					m_state = State.Failed;
				throw new MySqlException("Unable to connect to any of the specified MySQL hosts.");
			}

			var socketByteHandler = new SocketByteHandler(m_socket);
			m_payloadHandler = new StandardPayloadHandler(socketByteHandler);

			var payload = await ReceiveAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
			var reader = new ByteArrayReader(payload.ArraySegment.Array, payload.ArraySegment.Offset, payload.ArraySegment.Count);
			var initialHandshake = new InitialHandshakePacket(reader);

			// if PluginAuth is supported, then use the specified auth plugin; else, fall back to protocol capabilities to determine the auth type to use
			string authPluginName;
			if ((initialHandshake.ProtocolCapabilities & ProtocolCapabilities.PluginAuth) != 0)
				authPluginName = initialHandshake.AuthPluginName;
			else
				authPluginName = (initialHandshake.ProtocolCapabilities & ProtocolCapabilities.SecureConnection) == 0 ? "mysql_old_password" : "mysql_native_password";
			if (authPluginName != "mysql_native_password")
				throw new NotSupportedException("Authentication method '{0}' is not supported.".FormatInvariant(initialHandshake.AuthPluginName));

			ServerVersion = new ServerVersion(Encoding.ASCII.GetString(initialHandshake.ServerVersion));
			ConnectionId = initialHandshake.ConnectionId;
			AuthPluginData = initialHandshake.AuthPluginData;
			if (cs.UseCompression && (initialHandshake.ProtocolCapabilities & ProtocolCapabilities.Compress) == 0)
				cs = cs.WithUseCompression(false);

			var serverSupportsSsl = (initialHandshake.ProtocolCapabilities & ProtocolCapabilities.Ssl) != 0;
			if (cs.SslMode != MySqlSslMode.None && (cs.SslMode != MySqlSslMode.Preferred || serverSupportsSsl))
			{
				if (!serverSupportsSsl)
					throw new MySqlException("Server does not support SSL");
				await InitSslAsync(initialHandshake.ProtocolCapabilities, cs, ioBehavior, cancellationToken).ConfigureAwait(false);
			}

			var response = HandshakeResponse41Packet.Create(initialHandshake, cs);
			payload = new PayloadData(new ArraySegment<byte>(response));
			await SendReplyAsync(payload, ioBehavior, cancellationToken).ConfigureAwait(false);
			payload = await ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
			OkPayload.Create(payload);

			if (cs.UseCompression)
				m_payloadHandler = new CompressedPayloadHandler(m_payloadHandler.ByteHandler);
		}

		public async Task ResetConnectionAsync(ConnectionSettings cs, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			VerifyState(State.Connected);
			if (ServerVersion.Version.CompareTo(ServerVersions.SupportsResetConnection) >= 0)
			{
				await SendAsync(ResetConnectionPayload.Create(), ioBehavior, cancellationToken).ConfigureAwait(false);
				var payload = await ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
				OkPayload.Create(payload);

				// the "reset connection" packet also resets the connection charset, so we need to change that back to our default
				payload = new PayloadData(new ArraySegment<byte>(PayloadUtilities.CreateEofStringPayload(CommandKind.Query, "SET NAMES utf8mb4 COLLATE utf8mb4_bin;")));
				await SendAsync(payload, ioBehavior, cancellationToken).ConfigureAwait(false);
				payload = await ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
				OkPayload.Create(payload);
			}
			else
			{
				// optimistically hash the password with the challenge from the initial handshake (supported by MariaDB; doesn't appear to be supported by MySQL)
				var hashedPassword = AuthenticationUtility.CreateAuthenticationResponse(AuthPluginData, 0, cs.Password);
				var payload = ChangeUserPayload.Create(cs.UserID, hashedPassword, cs.Database);
				await SendAsync(payload, ioBehavior, cancellationToken).ConfigureAwait(false);
				payload = await ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
				if (payload.HeaderByte == AuthenticationMethodSwitchRequestPayload.Signature)
				{
					// if the server didn't support the hashed password; rehash with the new challenge
					var switchRequest = AuthenticationMethodSwitchRequestPayload.Create(payload);
					if (switchRequest.Name != "mysql_native_password")
						throw new NotSupportedException("Authentication method '{0}' is not supported.".FormatInvariant(switchRequest.Name));
					hashedPassword = AuthenticationUtility.CreateAuthenticationResponse(switchRequest.Data, 0, cs.Password);
					payload = new PayloadData(new ArraySegment<byte>(hashedPassword));
					await SendReplyAsync(payload, ioBehavior, cancellationToken).ConfigureAwait(false);
					payload = await ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
				}
				OkPayload.Create(payload);
			}
		}

		public async Task<bool> TryPingAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			VerifyState(State.Connected);

			// check if client socket is still connected
			// http://stackoverflow.com/questions/2661764/how-to-check-if-a-socket-is-connected-disconnected-in-c
			if (m_socket.Poll(1, SelectMode.SelectRead) && m_socket.Available == 0)
				return false;
			// client socket is still connected, send ping payload to verify server socket is still connected
			try
			{
				await SendAsync(PingPayload.Create(), ioBehavior, cancellationToken).ConfigureAwait(false);
				var payload = await ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
				OkPayload.Create(payload);
				return true;
			}
			catch (EndOfStreamException)
			{
			}
			catch (SocketException)
			{
			}

			VerifyState(State.Failed);
			return false;
		}

		// Starts a new conversation with the server by sending the first packet.
		public ValueTask<int> SendAsync(PayloadData payload, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			m_payloadHandler.StartNewConversation();
			return TryAsync(m_payloadHandler.WritePayloadAsync, payload.ArraySegment, ioBehavior, cancellationToken);
		}

		// Starts a new conversation with the server by receiving the first packet.
		public ValueTask<PayloadData> ReceiveAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			m_payloadHandler.StartNewConversation();
			return TryAsync(m_payloadHandler.ReadPayloadAsync, ioBehavior, cancellationToken);
		}

		// Continues a conversation with the server by receiving a response to a packet sent with 'Send' or 'SendReply'.
		public ValueTask<PayloadData> ReceiveReplyAsync(IOBehavior ioBehavior, CancellationToken cancellationToken) =>
			TryAsync(m_payloadHandler.ReadPayloadAsync, ioBehavior, cancellationToken);

		// Continues a conversation with the server by sending a reply to a packet received with 'Receive' or 'ReceiveReply'.
		public ValueTask<int> SendReplyAsync(PayloadData payload, IOBehavior ioBehavior, CancellationToken cancellationToken) =>
			TryAsync(m_payloadHandler.WritePayloadAsync, payload.ArraySegment, ioBehavior, cancellationToken);

		private void VerifyConnected()
		{
			lock (m_lock)
			{
				if (m_state == State.Closed)
					throw new ObjectDisposedException(nameof(MySqlSession));
				if (m_state != State.Connected && m_state != State.Querying && m_state != State.CancelingQuery && m_state != State.Closing)
					throw new InvalidOperationException("MySqlSession is not connected.");
			}
		}

		private async Task<bool> OpenTcpSocketAsync(ConnectionSettings cs, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			foreach (var hostname in cs.Hostnames)
			{
				IPAddress[] ipAddresses;
				try
				{
#if NETSTANDARD1_3
// Dns.GetHostAddresses isn't available until netstandard 2.0: https://github.com/dotnet/corefx/pull/11950
					ipAddresses = await Dns.GetHostAddressesAsync(hostname).ConfigureAwait(false);
#else
					if (ioBehavior == IOBehavior.Asynchronous)
					{
						ipAddresses = await Dns.GetHostAddressesAsync(hostname).ConfigureAwait(false);
					}
					else
					{
						ipAddresses = Dns.GetHostAddresses(hostname);
					}
#endif
				}
				catch (SocketException)
				{
					// name couldn't be resolved
					continue;
				}

				// need to try IP Addresses one at a time: https://github.com/dotnet/corefx/issues/5829
				foreach (var ipAddress in ipAddresses)
				{
					TcpClient tcpClient = null;
					try
					{
						tcpClient = new TcpClient(ipAddress.AddressFamily);
						using (cancellationToken.Register(() => tcpClient?.Client?.Dispose()))
						{
							try
							{
								if (ioBehavior == IOBehavior.Asynchronous)
								{
									await tcpClient.ConnectAsync(ipAddress, cs.Port).ConfigureAwait(false);
								}
								else
								{
#if NETSTANDARD1_3
									await tcpClient.ConnectAsync(ipAddress, cs.Port).ConfigureAwait(false);
#else
									tcpClient.Connect(ipAddress, cs.Port);
#endif
								}
							}
							catch (ObjectDisposedException ex) when (cancellationToken.IsCancellationRequested)
							{
								throw new MySqlException("Connect Timeout expired.", ex);
							}
						}
					}
					catch (SocketException)
					{
						tcpClient?.Client?.Dispose();
						continue;
					}

					m_hostname = hostname;
					m_tcpClient = tcpClient;
					m_socket = m_tcpClient.Client;
					m_networkStream = m_tcpClient.GetStream();
					SerializationUtility.SetKeepalive(m_socket, cs.Keepalive);
					lock (m_lock)
						m_state = State.Connected;
					return true;
				}
			}
			return false;
		}

		private async Task<bool> OpenUnixSocketAsync(ConnectionSettings cs, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
			var unixEp = new UnixEndPoint(cs.UnixSocket);
			try
			{
				using (cancellationToken.Register(() => socket.Dispose()))
				{
					try
					{
						if (ioBehavior == IOBehavior.Asynchronous)
						{
#if NETSTANDARD1_3
							await socket.ConnectAsync(unixEp).ConfigureAwait(false);
#else
							await Task.Factory.FromAsync(socket.BeginConnect, socket.EndConnect, unixEp, null).ConfigureAwait(false);
#endif
						}
						else
						{
							socket.Connect(unixEp);
						}
					}
					catch (ObjectDisposedException ex) when (cancellationToken.IsCancellationRequested)
					{
						throw new MySqlException("Connect Timeout expired.", ex);
					}
				}
			}
			catch (SocketException)
			{
				socket.Dispose();
			}

			if (socket.Connected)
			{
				m_socket = socket;
				m_networkStream = new NetworkStream(socket);

				lock (m_lock)
					m_state = State.Connected;
				return true;
			}

			return false;
		}

		private async Task InitSslAsync(ProtocolCapabilities serverCapabilities, ConnectionSettings cs, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			X509CertificateCollection clientCertificates = null;
			if (cs.CertificateFile != null)
			{
				try
				{
					var certificate = new X509Certificate2(cs.CertificateFile, cs.CertificatePassword);
					clientCertificates = new X509CertificateCollection {certificate};
				}
				catch (CryptographicException ex)
				{
					if (!File.Exists(cs.CertificateFile))
						throw new MySqlException("Cannot find SSL Certificate File", ex);
					throw new MySqlException("Either the SSL Certificate Password is incorrect or the SSL Certificate File is invalid", ex);
				}
			}

			Func<object, string, X509CertificateCollection, X509Certificate, string[], X509Certificate> localCertificateCb =
				(lcbSender, lcbTargetHost, lcbLocalCertificates, lcbRemoteCertificate, lcbAcceptableIssuers) => lcbLocalCertificates[0];

			Func<object, X509Certificate, X509Chain, SslPolicyErrors, bool> remoteCertificateCb =
				(rcbSender, rcbCertificate, rcbChain, rcbPolicyErrors) =>
				{
					switch (rcbPolicyErrors)
					{
						case SslPolicyErrors.None:
							return true;
						case SslPolicyErrors.RemoteCertificateNameMismatch:
							return cs.SslMode != MySqlSslMode.VerifyFull;
						default:
							return cs.SslMode == MySqlSslMode.Preferred || cs.SslMode == MySqlSslMode.Required;
					}
				};

			SslStream sslStream;
			if (clientCertificates == null)
				sslStream = new SslStream(m_networkStream, false,
					new RemoteCertificateValidationCallback(remoteCertificateCb));
			else
				sslStream = new SslStream(m_networkStream, false,
					new RemoteCertificateValidationCallback(remoteCertificateCb),
					new LocalCertificateSelectionCallback(localCertificateCb));

			// SslProtocols.Tls1.2 throws an exception in Windows, see https://github.com/mysql-net/MySqlConnector/pull/101
			var sslProtocols = SslProtocols.Tls | SslProtocols.Tls11;
			if (!Utility.IsWindows())
				sslProtocols |= SslProtocols.Tls12;

			var checkCertificateRevocation = cs.SslMode == MySqlSslMode.VerifyFull;

			var initSsl = new PayloadData(new ArraySegment<byte>(HandshakeResponse41Packet.InitSsl(serverCapabilities, cs)));
			await SendReplyAsync(initSsl, ioBehavior, cancellationToken).ConfigureAwait(false);

			try
			{
				if (ioBehavior == IOBehavior.Asynchronous)
				{
					await sslStream.AuthenticateAsClientAsync(m_hostname, clientCertificates, sslProtocols, checkCertificateRevocation).ConfigureAwait(false);
				}
				else
				{
#if NETSTANDARD1_3
					await sslStream.AuthenticateAsClientAsync(m_hostname, clientCertificates, sslProtocols, checkCertificateRevocation).ConfigureAwait(false);
#else
					sslStream.AuthenticateAsClient(m_hostname, clientCertificates, sslProtocols, checkCertificateRevocation);
#endif
				}
				var sslByteHandler = new StreamByteHandler(sslStream);
				m_payloadHandler.ByteHandler = sslByteHandler;
			}
			catch (Exception ex)
			{
				ShutdownSocket();
				m_hostname = "";
				lock (m_lock)
					m_state = State.Failed;
				if (ex is AuthenticationException)
					throw new MySqlException("SSL Authentication Error", ex);
				if (ex is IOException && clientCertificates != null)
					throw new MySqlException("MySQL Server rejected client certificate", ex);
				throw;
			}
		}

		private void ShutdownSocket()
		{
			m_payloadHandler = null;
			if (m_networkStream != null)
			{
#if NETSTANDARD1_3
				m_networkStream.Dispose();
#else
				m_networkStream.Close();
#endif
				m_networkStream = null;
			}
			if (m_tcpClient != null)
			{
				try
				{
#if NETSTANDARD1_3
					m_tcpClient.Dispose();
#else
					m_tcpClient.Close();
#endif
				}
				catch (SocketException)
				{
				}
				m_tcpClient = null;
				m_socket = null;
			}
			else if (m_socket != null)
			{
				try
				{
					m_socket.Dispose();
					m_socket = null;
				}
				catch (SocketException)
				{
				}
			}
		}

		private ValueTask<int> TryAsync<TArg>(Func<TArg, IOBehavior, ValueTask<int>> func, TArg arg, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			ValueTask<int> task;
			try
			{
				VerifyConnected();
				task = func(arg, ioBehavior);
			}
			catch (Exception ex)
			{
				task = ValueTaskExtensions.FromException<int>(ex);
			}

			if (task.IsCompletedSuccessfully)
				return task;

			return new ValueTask<int>(task.AsTask()
				.ContinueWith(TryAsyncContinuation, cancellationToken, TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
		}

		private int TryAsyncContinuation(Task<int> task)
		{
			if (task.IsFaulted)
			{
				SetFailed();
				task.GetAwaiter().GetResult();
			}
			return 0;
		}

		private ValueTask<PayloadData> TryAsync(Func<ProtocolErrorBehavior, IOBehavior, ValueTask<ArraySegment<byte>>> func, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			ValueTask<ArraySegment<byte>> task;
			try
			{
				VerifyConnected();
				task = func(ProtocolErrorBehavior.Throw, ioBehavior);
			}
			catch (Exception ex)
			{
				task = ValueTaskExtensions.FromException<ArraySegment<byte>>(ex);
			}

			if (task.IsCompletedSuccessfully)
			{
				var payload = new PayloadData(task.Result);
				if (payload.HeaderByte != ErrorPayload.Signature)
					return new ValueTask<PayloadData>(payload);

				var exception = ErrorPayload.Create(payload).ToException();
				return ValueTaskExtensions.FromException<PayloadData>(exception);
			}

			return new ValueTask<PayloadData>(task.AsTask()
				.ContinueWith(TryAsyncContinuation, cancellationToken, TaskContinuationOptions.LazyCancellation | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
		}

		private PayloadData TryAsyncContinuation(Task<ArraySegment<byte>> task)
		{
			if (task.IsFaulted)
				SetFailed();
			var payload = new PayloadData(task.GetAwaiter().GetResult());
			payload.ThrowIfError();
			return payload;
		}

		private void SetFailed()
		{
			lock (m_lock)
				m_state = State.Failed;
		}


		private void VerifyState(State state)
		{
			if (m_state != state)
				throw new InvalidOperationException("Expected state to be {0} but was {1}.".FormatInvariant(state, m_state));
		}

		private void VerifyState(State state1, State state2)
		{
			if (m_state != state1 && m_state != state2)
				throw new InvalidOperationException("Expected state to be ({0}|{1}) but was {2}.".FormatInvariant(state1, state2, m_state));
		}

		private enum State
		{
			// The session has been created; no connection has been made.
			Created,

			// The session is attempting to connect to a server.
			Connecting,

			// The session is connected to a server; there is no active query.
			Connected,

			// The session is connected to a server and a query is being made.
			Querying,

			// The session is connected to a server and the active query is being cancelled.
			CancelingQuery,

			// The session is closing.
			Closing,

			// The session is closed.
			Closed,

			// An unexpected error occurred; the session is in an unusable state.
			Failed,
		}

		readonly object m_lock;
		State m_state;
		string m_hostname = "";
		TcpClient m_tcpClient;
		Socket m_socket;
		NetworkStream m_networkStream;
		IPayloadHandler m_payloadHandler;
		MySqlCommand m_activeCommand;
		MySqlDataReader m_activeReader;
	}
}
