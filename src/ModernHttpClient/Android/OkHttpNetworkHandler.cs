using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Java.IO;
using Java.Net;
using Java.Util.Concurrent;
using Javax.Net.Ssl;
using Square.OkHttp3;

namespace ModernHttpClient
{
    public class NativeMessageHandler : HttpClientHandler
    {
        private OkHttpClient client;
        readonly CacheControl noCacheCacheControl = default (CacheControl);
        readonly bool throwOnCaptiveNetwork;

        readonly Dictionary<HttpRequestMessage, WeakReference> registeredProgressCallbacks =
            new Dictionary<HttpRequestMessage, WeakReference> ();
        readonly Dictionary<string, string> headerSeparators =
            new Dictionary<string, string> (){
                {"User-Agent", " "}
            };

        public bool CustomSSLVerification
        {
            get;
            private set;
        }

        public bool DisableCaching { get; set; }

        private TimeSpan? _timeout;
        public TimeSpan? Timeout
        {
            get { return _timeout; }
            set
            {
                _timeout = value;
                RefreshClient();
            }
        }

        public NativeMessageHandler () : this (false, false)
        {
        }

        public NativeMessageHandler (bool throwOnCaptiveNetwork, bool customSSLVerification, NativeCookieHandler cookieHandler = null)
        {
            this.throwOnCaptiveNetwork = throwOnCaptiveNetwork;
            this.CustomSSLVerification = customSSLVerification;

            RefreshClient();

            noCacheCacheControl = (new CacheControl.Builder()).NoCache().Build();
        }

        public void RegisterForProgress (HttpRequestMessage request, ProgressDelegate callback)
        {
            if (callback == null && registeredProgressCallbacks.ContainsKey (request)) {
                registeredProgressCallbacks.Remove (request);
                return;
            }

            registeredProgressCallbacks [request] = new WeakReference (callback);
        }
        
        public void RebuildClient( Action<OkHttpClient.Builder> customBuild )
        {
            if (client != null)
            {
                // This should finish out any in flight requests
                client.Dispatcher().ExecutorService().Shutdown();
                client = null;
            }

            var builder = new OkHttpClient.Builder();
            CommonClientBuilder(builder);
            customBuild?.Invoke(builder);
            client = builder.Build();
        }

        private void RefreshClient()
        {
            RebuildClient(null);
        }

        private void CommonClientBuilder(OkHttpClient.Builder builder)
        {
            if (CustomSSLVerification) {
                builder.HostnameVerifier(new HostnameVerifier());
            }
            
            if(Timeout != null) {
                var timeout = (long)Timeout.Value.TotalMilliseconds;
                builder.ConnectTimeout(timeout, TimeUnit.Milliseconds)
                    .WriteTimeout(timeout, TimeUnit.Milliseconds)
                    .ReadTimeout(timeout, TimeUnit.Milliseconds);
            }
        }

        ProgressDelegate getAndRemoveCallbackFromRegister (HttpRequestMessage request)
        {
            ProgressDelegate emptyDelegate = delegate { };

            lock (registeredProgressCallbacks) {
                if (!registeredProgressCallbacks.ContainsKey (request)) return emptyDelegate;

                var weakRef = registeredProgressCallbacks [request];
                if (weakRef == null) return emptyDelegate;

                var callback = weakRef.Target as ProgressDelegate;
                if (callback == null) return emptyDelegate;

                registeredProgressCallbacks.Remove (request);
                return callback;
            }
        }

        string getHeaderSeparator (string name)
        {
            if (headerSeparators.ContainsKey (name)) {
                return headerSeparators [name];
            }

            return ",";
        }

        protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var java_uri = request.RequestUri.GetComponents (UriComponents.AbsoluteUri, UriFormat.UriEscaped);
            var url = new Java.Net.URL (java_uri);

            var body = default (RequestBody);
            if (request.Content != null) {
                var bytes = await request.Content.ReadAsByteArrayAsync ().ConfigureAwait (false);

                var contentType = "text/plain";
                if (request.Content.Headers.ContentType != null) {
                    contentType = String.Join (" ", request.Content.Headers.GetValues ("Content-Type"));
                }
                body = RequestBody.Create (MediaType.Parse (contentType), bytes);
            }

            var builder = new Request.Builder ()
                .Method (request.Method.Method.ToUpperInvariant (), body)
                .Url (url);

            if (DisableCaching) {
                builder.CacheControl (noCacheCacheControl);
            }

            var keyValuePairs = request.Headers
                .Union (request.Content != null ?
                    (IEnumerable<KeyValuePair<string, IEnumerable<string>>>)request.Content.Headers :
                    Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>> ());

            foreach (var kvp in keyValuePairs) builder.AddHeader (kvp.Key, String.Join (getHeaderSeparator (kvp.Key), kvp.Value));

            cancellationToken.ThrowIfCancellationRequested ();

            var rq = builder.Build ();
            var call = client.NewCall (rq);

            // NB: Even closing a socket must be done off the UI thread. Cray!
            cancellationToken.Register (() => Task.Run (() => call.Cancel ()));

            var resp = default (Response);
            try {
                resp = await call.EnqueueAsync ().ConfigureAwait (false);
                var newReq = resp.Request ();
                var newUri = newReq == null ? null : newReq.Url ();
                request.RequestUri = new Uri (newUri.ToString ());
                if (throwOnCaptiveNetwork && newUri != null) {
                    if (url.Host != newUri.Host()) {
                        throw new CaptiveNetworkException (new Uri (java_uri), new Uri (newUri.ToString ()));
                    }
                }
            } catch (IOException ex) {
                if (ex.Message.ToLowerInvariant ().Contains ("canceled")) {
                    throw new System.OperationCanceledException ();
                }

                throw;
            }

            var respBody = resp.Body ();

            cancellationToken.ThrowIfCancellationRequested ();

            var ret = new HttpResponseMessage ((HttpStatusCode)resp.Code ());
            ret.RequestMessage = request;
            ret.ReasonPhrase = resp.Message ();
            if (respBody != null) {
                var content = new ProgressStreamContent (respBody.ByteStream (), CancellationToken.None);
                content.Progress = getAndRemoveCallbackFromRegister (request);
                ret.Content = content;
            } else {
                ret.Content = new ByteArrayContent (new byte [0]);
            }

            var respHeaders = resp.Headers ();
            foreach (var k in respHeaders.Names ()) {
                ret.Headers.TryAddWithoutValidation (k, respHeaders.Get (k));
                ret.Content.Headers.TryAddWithoutValidation (k, respHeaders.Get (k));
            }

            return ret;
        }


    }

    public static class AwaitableOkHttp
    {
        public static Task<Response> EnqueueAsync (this ICall This)
        {
            var cb = new OkTaskCallback ();
            This.Enqueue (cb);

            return cb.Task;
        }

        class OkTaskCallback : Java.Lang.Object, ICallback
        {
            readonly TaskCompletionSource<Response> tcs = new TaskCompletionSource<Response> ();
            public Task<Response> Task { get { return tcs.Task; } }

            public void OnFailure(ICall call, IOException exception)
            {
                // Kind of a hack, but the simplest way to find out that server cert. validation failed
                if (exception.Message == String.Format("Hostname '{0}' was not verified", call.Request().Url().Host()))
                {
                    tcs.TrySetException(new WebException(exception.LocalizedMessage, WebExceptionStatus.TrustFailure));
                }
                else if (exception is UnknownHostException)
                {
                    tcs.TrySetException(new System.OperationCanceledException());
                }
                else
                {
                    tcs.TrySetException(exception);
                }
            }

            public void OnResponse(ICall call, Response response)
            {
                tcs.TrySetResult(response);
            }
        }
    }

    class HostnameVerifier : Java.Lang.Object, IHostnameVerifier
    {
        public bool Verify (string hostname, ISSLSession session)
        {
            return verifyServerCertificate (hostname, session);
        }

        /// <summary>
        /// Verifies the server certificate by calling into ServicePointManager.ServerCertificateValidationCallback or,
        /// if the is no delegate attached to it by using the default hostname verifier.
        /// </summary>
        /// <returns><c>true</c>, if server certificate was verifyed, <c>false</c> otherwise.</returns>
        /// <param name="hostname"></param>
        /// <param name="session"></param>
        static bool verifyServerCertificate (string hostname, ISSLSession session)
        {
            var defaultVerifier = HttpsURLConnection.DefaultHostnameVerifier;

            if (ServicePointManager.ServerCertificateValidationCallback == null) {
                return defaultVerifier.Verify(hostname, session);
            }

            // Convert java certificates to .NET certificates and build cert chain from root certificate
            var certificates = session.GetPeerCertificateChain ();
            var chain = new X509Chain ();
            var netCerts = certificates.Select (x => new X509Certificate2 (x.GetEncoded ())).ToArray ();

            for (int i = 1; i < netCerts.Length; i++) {
                chain.ChainPolicy.ExtraStore.Add (netCerts [i]);
            }

            X509Certificate2 root = netCerts [0];

            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.UrlRetrievalTimeout = new TimeSpan (0, 1, 0);
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

            chain.Build (root);

            //If the callback returns true, then we want it to continue with the default validation. The call back is only responsible for 
            //certificate pinning. Nothing else. 
            //Unless, of course, you want to use a self-signed cert in which case the above comment is incorrect.
            if (ServicePointManager.ServerCertificateValidationCallback (hostname, root, chain, System.Net.Security.SslPolicyErrors.None)) {
                return true;
            }

            return false;
        }
    }
}
