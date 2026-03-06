using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

using ConduitNet;
using ConduitNet.Utility;

namespace ConduitNet.Http {
    /// <summary>
    /// Fluent builder for UnityWebRequest.
    /// <code>
    /// yield return HttpRequest.Get("https://api.example.com/users")
    ///     .SetHeader("X-Custom", "value")
    ///     .SetBearerToken(token)
    ///     .SetQueryParams(new() { ["page"] = 1, ["limit"] = 10 })
    ///     .Send(onSuccess: res => Debug.Log(res.Text),
    ///           onError: err => Debug.LogError(err));
    /// </code>
    /// </summary>
    public class HttpRequest {
        private string _url;
        private string _method;
        private byte[] _body;
        private string _contentType = "application/json";
        private readonly Dictionary<string, string> _headers = new();
        private IDictionary<string, object> _queryParams;
        private int _timeout = 30;

        private HttpRequest(string method, string url) {
            _method = method;
            _url = url;
        }

        // ── Factory Methods ─────────────────────────────────────

        /// <summary>Creates a GET request.</summary>
        public static HttpRequest Get(string url) => new("GET", url);
        /// <summary>Creates a POST request.</summary>
        public static HttpRequest Post(string url) => new("POST", url);
        /// <summary>Creates a PUT request.</summary>
        public static HttpRequest Put(string url) => new("PUT", url);
        /// <summary>Creates a PATCH request.</summary>
        public static HttpRequest Patch(string url) => new("PATCH", url);
        /// <summary>Creates a DELETE request.</summary>
        public static HttpRequest Delete(string url) => new("DELETE", url);

        // ── Headers ─────────────────────────────────────────────

        /// <summary>Adds a header to the request.</summary>
        public HttpRequest SetHeader(string key, string value) {
            _headers[key] = value;
            return this;
        }

        /// <summary>Adds multiple headers to the request.</summary>
        public HttpRequest SetHeaders(Dictionary<string, string> headers) {
            foreach (var kvp in headers)
                _headers[kvp.Key] = kvp.Value;
            return this;
        }

        /// <summary>Sets a Bearer token authorization header.</summary>
        public HttpRequest SetBearerToken(string token) {
            _headers["Authorization"] = $"Bearer {token}";
            return this;
        }

        /// <summary>Sets a Basic authorization header.</summary>
        public HttpRequest SetBasicAuth(string username, string password) {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _headers["Authorization"] = $"Basic {credentials}";
            return this;
        }

        // ── Body ────────────────────────────────────────────────

        /// <summary>Serializes an object to JSON and sets it as the request body.</summary>
        public HttpRequest SetBody(object body) {
            _body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
            _contentType = "application/json";
            return this;
        }

        /// <summary>Sets a raw string as the request body.</summary>
        public HttpRequest SetBody(string rawBody, string contentType = "text/plain") {
            _body = Encoding.UTF8.GetBytes(rawBody);
            _contentType = contentType;
            return this;
        }

        /// <summary>Sets a raw byte array as the request body.</summary>
        public HttpRequest SetBody(byte[] rawBody, string contentType = "application/octet-stream") {
            _body = rawBody;
            _contentType = contentType;
            return this;
        }

        // ── Query Params ────────────────────────────────────────

        /// <summary>Sets query parameters which will be appended to the URL.</summary>
        public HttpRequest SetQueryParams(IDictionary<string, object> queryParams) {
            _queryParams = queryParams;
            return this;
        }

        // ── Options ─────────────────────────────────────────────

        /// <summary>Sets the request timeout in seconds.</summary>
        public HttpRequest SetTimeout(int seconds) {
            _timeout = seconds;
            return this;
        }

        /// <summary>Sets the Content-Type header.</summary>
        public HttpRequest SetContentType(string contentType) {
            _contentType = contentType;
            return this;
        }

        // ── Send ────────────────────────────────────────────────

        /// <summary>Sends the HTTP request.</summary>
        public IEnumerator Send(Action<HttpResponse> onSuccess = null, Action<string> onError = null) {
            string finalUrl = _queryParams != null
                ? QueryParamBuilder.BuildUrl(_url, _queryParams)
                : _url;

            var request = new UnityWebRequest(finalUrl, _method);
            try {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = _timeout;

                if (_body != null) {
                    request.uploadHandler = new UploadHandlerRaw(_body);
                }

                request.SetRequestHeader("Content-Type", _contentType);

                foreach (var header in _headers) {
                    request.SetRequestHeader(header.Key, header.Value);
                }

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success) {
                    onSuccess?.Invoke(new HttpResponse(request));
                }
                else {
                    onError?.Invoke(request.error);
                }
            }
            finally {
                request.Dispose();
            }
        }

        /// <summary>
        /// Sends the request and deserializes the response body as T.
        /// </summary>
        public IEnumerator Send<T>(Action<T> onSuccess, Action<string> onError = null) {
            yield return Send(
                onSuccess: res => {
                    try {
                        var data = res.Deserialize<T>();
                        onSuccess?.Invoke(data);
                    }
                    catch (Exception ex) {
                        onError?.Invoke($"Deserialization failed: {ex.Message}");
                    }
                },
                onError: onError
            );
        }
    }

    /// <summary>Wrapper for an HTTP response.</summary>
    public class HttpResponse {
        /// <summary>HTTP status code.</summary>
        public long StatusCode { get; }
        /// <summary>Response body as text.</summary>
        public string Text { get; }
        /// <summary>Response body as a byte array.</summary>
        public byte[] Bytes { get; }
        /// <summary>Response headers.</summary>
        public Dictionary<string, string> Headers { get; }

        internal HttpResponse(UnityWebRequest request) {
            StatusCode = request.responseCode;
            Text = request.downloadHandler?.text;
            Bytes = request.downloadHandler?.data;

            Headers = new();
            var responseHeaders = request.GetResponseHeaders();
            if (responseHeaders != null) {
                foreach (var key in responseHeaders.Keys) {
                    Headers[key] = responseHeaders[key];
                }
            }
        }

        /// <summary>Deserializes the response body JSON to the specified type.</summary>
        public T Deserialize<T>() => JsonConvert.DeserializeObject<T>(Text);
    }
}
