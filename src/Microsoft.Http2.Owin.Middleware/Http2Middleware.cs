﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin;
using Microsoft.Http2.Protocol;
using Microsoft.Http2.Protocol.IO;
using ProtocolAdapters;

namespace ServerOwinMiddleware
{
    using AppFunc = Func<IDictionary<string, object>, Task>;
    using UpgradeDelegate = Action<IDictionary<string, object>, Func<IDictionary<string, object>, Task>>;
    // Http-01/2.0 uses a similar upgrade handshake to WebSockets. This middleware answers upgrade requests
    // using the Opaque Upgrade OWIN extension and then switches the pipeline to HTTP/2.0 binary framing.
    // Interestingly the HTTP/2.0 handshake does not need to be the first HTTP/1.1 request on a connection, only the last.
    public class Http2Middleware
    {
        // Pass requests onto this pipeline if not upgrading to HTTP/2.0.
        private readonly AppFunc _next;

        public Http2Middleware(AppFunc next)
        {
            _next = next;
        }

        /// <summary>
        /// Invokes the specified environment.
        /// This method is used for handshake.
        /// </summary>
        /// <param name="environment">The environment.</param>
        /// <returns></returns>
        public async Task Invoke(IDictionary<string, object> environment)
        {
            var request = new OwinRequest(environment);

            if (IsOpaqueUpgradePossible(request) && IsRequestForHttp2Upgrade(request))
            {
                var upgradeDelegate = environment["opaque.Upgrade"] as UpgradeDelegate;

                var trInfo = CreateTransportInfo(request);

                upgradeDelegate.Invoke(new Dictionary<string, object>(), opaque =>
                    {
                        //use the same stream which was used during upgrade
                        var opaqueStream = opaque["opaque.Stream"] as DuplexStream;

                        //Provide cancellation token here
                        var http2Adapter = new Http2OwinAdapter(opaqueStream, trInfo, Invoke, CancellationToken.None);

                        return http2Adapter.StartSession(GetInitialRequestParams(opaque));
                    });

                return;
            }

            //If we dont have upgrade delegate then pass request to the next layer
            await _next(environment);
        }

        private static bool IsRequestForHttp2Upgrade(OwinRequest request)
        {
            var headers = request.Environment["owin.RequestHeaders"] as IDictionary<string, string[]>;
            return  headers.ContainsKey("Connection")
                    && headers.ContainsKey("HTTP2-Settings")
                    && headers.ContainsKey("Upgrade") 
                    && headers["Upgrade"].FirstOrDefault(it =>
                                         it.ToUpper().IndexOf("HTTP", StringComparison.Ordinal) != -1 &&
                                         it.IndexOf("2.0", StringComparison.Ordinal) != -1) != null;
        }

        private static bool IsOpaqueUpgradePossible(OwinRequest request)
        {
            var environment = request.Environment;

            return environment.ContainsKey("opaque.Upgrade")
                   && environment["opaque.Upgrade"] is UpgradeDelegate;
        }

        private IDictionary<string, string> GetInitialRequestParams(IDictionary<string, object> properties)
        {
            var request = new OwinRequest(properties);

            var defaultWindowSize = 200000.ToString();
            var defaultMaxStreams = 100.ToString();

            bool areSettingsOk = true;

            var path = !String.IsNullOrEmpty(request.Path)
                            ? request.Path
                            : "/index.html";
            var method = !String.IsNullOrEmpty(request.Method)
                            ? request.Method
                            : "get";


            var splittedSettings = new string[0];
            try
            {
                var settingsBytes = Convert.FromBase64String(request.Headers["Http2-Settings"]);
                var http2Settings = Encoding.UTF8.GetString(settingsBytes);
                if (http2Settings.IndexOf(',') != -1)
                {
                    splittedSettings = http2Settings.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
                else
                {
                    areSettingsOk = false;
                }

                if (splittedSettings.Length < 2)
                {
                    areSettingsOk = false;
                }
            }
            catch (Exception)
            {
                areSettingsOk = false;
            }

            var windowSize = areSettingsOk ? splittedSettings[0].Trim() : defaultWindowSize;
            var maxStreams = areSettingsOk ? splittedSettings[1].Trim() : defaultMaxStreams;

            return new Dictionary<string, string>
                {
                    //Add more headers
                    {":path", path},
                    {":method", method},
                    {":initial_window_size", windowSize},
                    {":max_concurrent_streams", maxStreams},
                };
        }
        

        private TransportInformation CreateTransportInfo(OwinRequest owinRequest)
        {
            return new TransportInformation
            {
                RemoteIpAddress = owinRequest.RemoteIpAddress,
                RemotePort = owinRequest.RemotePort != null ? (int) owinRequest.RemotePort : 8080,
                LocalIpAddress = owinRequest.LocalIpAddress,
                LocalPort = owinRequest.LocalPort != null ? (int) owinRequest.LocalPort : 8080,
            };
        }
    }
}