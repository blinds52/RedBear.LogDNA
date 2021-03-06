﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RedBear.LogDNA.Properties;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Timers;
using WebSocketSharp;
using Timer = System.Timers.Timer;

namespace RedBear.LogDNA
{
    /// <summary>
    /// Main class for communicating with the LogDNA servers.
    /// </summary>
    public class ApiClient : IApiClient
    {
        private JObject _result;
        private WebSocket _ws;
        private int _connectionAttempt;

        private readonly ConcurrentQueue<LogLine> _buffer = new ConcurrentQueue<LogLine>();
        private readonly ConcurrentDictionary<string, int> _flags = new ConcurrentDictionary<string, int>();

        internal Config Configuration { get; set; }

        /// <inheritdoc />
        /// <summary>
        /// Whether the client is connectead to LogDNA.
        /// </summary>
        /// <returns></returns>
        public bool Active { get; set; }

        /// <inheritdoc />
        /// <summary>
        /// Log the internal operations of the LogDNA client to the Console window.
        /// </summary>
        /// <returns></returns>
        public bool LogInternalsToConsole { get; set; }

        /// <summary>
        /// Creates a new ApiClient using the specified configuration.
        /// </summary>
        /// <param name="config">The configuration received from the LogDNA servers</param>
        public ApiClient(Config config)
        {
            Configuration = config;

            var timer = new Timer(Configuration.FlushInterval);
            timer.Elapsed += _timer_Elapsed;
            timer.Start();
        }

        /// <inheritdoc />
        /// <summary>
        /// Connects to the LogDNA servers using the specified configuration.
        /// </summary>
        /// <returns></returns>
        public void Connect()
        {
            if (StartConnect())
            {
                try
                {
                    var url = new Uri("https://api.logdna.com/authenticate/");
                    var status = PostData(url, Configuration);

                    if (status == HttpStatusCode.OK && _result?["apiserver"] != null && _result["apiserver"].ToString() != url.Host)
                    {
                        if (_result["ssl"].ToString() == "true")
                        {
                            url = new Uri($"https://{_result["apiserver"]}/authenticate/");
                            status = PostData(url, Configuration);
                        }
                    }

                    if (status != HttpStatusCode.OK)
                    {
                        InternalLogger("Auth failed; retry after a delay.");
                        Thread.Sleep(Configuration.AuthFailDelay);
                        Connect();
                        return;
                    }

                    if (_result != null)
                    {
                        Configuration.AuthToken = _result["token"].ToString();
                        Configuration.LogServer = _result["server"].ToString();
                        Configuration.LogServerPort = _result["port"].Value<int>();
                        Configuration.LogServerSsl = _result["ssl"].Value<bool>();

                        ConnectSocket();
                    }
                }
                catch (Exception)
                {
                    EndConnect();
                }

            }

        }

        /// <summary>
        /// Connects to the LogDNA websocket server.
        /// </summary>
        private void ConnectSocket()
        {
            try
            {
                if (_ws != null)
                {
                    try
                    {
                        Active = false;
                        if (_ws.ReadyState != WebSocketState.Closed)
                            _ws.Close();
                    }
                    catch (Exception)
                    {
                        // Do Nothing
                    }
                }

                var protocol = "ws";
                if (Configuration.LogServerSsl) protocol += "s";

                _ws =
                    new WebSocket(
                        $"{protocol}://{Configuration.LogServer}:{Configuration.LogServerPort}/?auth_token={WebUtility.UrlEncode(Configuration.AuthToken)}&timestamp={DateTime.Now.ToJavaTimestamp()}&compress=1&tailmode=&transport=")
                    {
                        Compression = CompressionMethod.Deflate
                    };

                _ws.OnOpen += Ws_OnOpen;
                _ws.OnClose += Ws_OnClose;
                _ws.OnError += Ws_OnError;
                _ws.OnMessage += Ws_OnMessage;

                InternalLogger("Connecting web socket..");
                _ws.Connect();
            }
            catch (Exception ex)
            {
                InternalLogger("Exception while connecting to LogDNA websocket");
                InternalLogger(ex.ToString());
                Reconnect();
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// Disconnects the client from the LogDNA servers.
        /// </summary>
        public void Disconnect()
        {
            InternalLogger("Disconnecting..");
            Flush();
            Active = false;
            _ws?.Close();
        }

        private void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            try
            {
                var data = JObject.Parse(e.Data);

                InternalLogger($"Received {data["e"].Value<string>()} command..");

                switch (data["e"].Value<string>())
                {
                    case "u":
                        // Do Nothing - this library doesn't allow auto-updating.
                        break;
                    case "r":
                        Connect();
                        break;
                    case "p":
                        // Ping Pong
                        break;
                    default:
                        throw new LogDNAException(Resources.UnknownCommand);
                }
            }
            catch (JsonException ex)
            {
                InternalLogger("Deserialisation exception..");
                InternalLogger(ex.ToString());
                throw new LogDNAException(Resources.InvalidCommand);
            }
        }

        private void Reconnect()
        {
            if (StartReconnect())
            {
                try
                {
                    InternalLogger("Reconnecting..");

                    _connectionAttempt += 1;
                    var timeout = 1000 * Math.Pow(1.5, _connectionAttempt);
                    if (timeout > 5000) timeout = 5000;
                    Thread.Sleep((int)timeout);

                    Connect();
                }
                finally
                {
                    EndReconnect();
                }
            }
        }

        private void Ws_OnError(object sender, ErrorEventArgs e)
        {
            InternalLogger($"Error received: {e.Message}");
            if (Active)
            {
                Reconnect();
            }
        }

        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            InternalLogger("Connection closed..");
        }

        private void Ws_OnOpen(object sender, EventArgs e)
        {
            InternalLogger("Connected successfully!");
            Active = true;
            _connectionAttempt = 0;
            EndConnect();
        }

        private HttpStatusCode PostData(Uri url, Config config)
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = url;
                client.DefaultRequestHeaders.Add("User-Agent", config.UserAgent);

                var response = client.PostAsync(config.IngestionKey,
                    new StringContent(JsonConvert.SerializeObject(config), Encoding.UTF8, "application/json")).Result;

                _result = response.IsSuccessStatusCode
                    ? JObject.Parse(response.Content.ReadAsStringAsync().Result)
                    : null;

                InternalLogger(_result?.ToString());

                return response.StatusCode;
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// Sends the specified message directly to the websocket.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns>True if the message was transmitted successfully.</returns>
        public bool Send(string message)
        {
            InternalLogger($"Sending message: \"{message}\"..");
            InternalLogger($"ReadyState: {_ws.ReadyState}");

            if (_ws.ReadyState == WebSocketState.Closed)
            {
                Reconnect();
            }

            if (_ws.ReadyState == WebSocketState.Open)
            {
                try
                {
                    _ws.Send(message);
                    InternalLogger("Sent successfully!");
                    return true;
                }
                catch (Exception ex)
                {
                    InternalLogger("Exception sending message..");
                    InternalLogger(ex.ToString());
                    return false;
                }
            }
            return false;
        }

        private void _timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Flush();
        }

        /// <summary>
        /// Adds a LogLine to the buffer.
        /// </summary>
        /// <param name="line">The line.</param>
        public void AddLine(LogLine line)
        {
            _buffer.Enqueue(line);
        }

        /// <summary>
        /// Attempts to flush the buffer.
        /// </summary>
        public void Flush()
        {
            if (StartFlush())
            {
                try
                {
                    var length = _buffer.Count;
                    var items = new List<LogLine>();

                    for (var i = 0; i < length; i++)
                    {
                        _buffer.TryDequeue(out var line);

                        if (line == null) break;

                        items.Add(line);
                    }

                    if (items.Any())
                    {
                        var message = new BufferMessage
                        {
                            LogLines = items
                        };

                        try
                        {
                            Send(JsonConvert.SerializeObject(message));
                        }
                        catch (Exception)
                        {
                            // Do nothing
                        }
                    }
                }
                finally
                {
                    EndFlush();
                }
            }
        }

        private bool StartFlush()
        {
            return _flags.AddOrUpdate("flushing", 1, (s, i) => i + 1) == 1;
        }

        private void EndFlush()
        {
            _flags.TryRemove("flushing", out var _);
        }

        private bool StartReconnect()
        {
            return _flags.AddOrUpdate("reconnect", 1, (s, i) => i + 1) == 1;
        }

        private void EndReconnect()
        {
            _flags.TryRemove("reconnect", out var _);
        }

        private bool StartConnect()
        {
            return _flags.AddOrUpdate("connect", 1, (s, i) => i + 1) == 1;
        }

        private void EndConnect()
        {
            _flags.TryRemove("connect", out var _);
        }

        private void InternalLogger(string message)
        {
            if (LogInternalsToConsole)
                Console.WriteLine(message);
        }
    }
}
