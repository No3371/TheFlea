using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace TheFlea
{
    internal static class MarketClient
    {
        private const int TimeoutSeconds = 10;
        private const int MaxAttempts = 3;
        private const float RetryDelaySeconds = 3f;

        private static string Url(string path) => ModEntry.ServerUrl.Value.TrimEnd('/') + path;

        // "failed" gets the error and the HTTP status, or 0 when no response arrived.
        public static IEnumerator Post<TResponse>(string path, object body, Action<TResponse> done, Action<string, long> failed)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
            return Send(() =>
            {
                UnityWebRequest request = new UnityWebRequest(Url(path), "POST");
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                return request;
            }, done, failed);
        }

        // A sleeping server can fail or time out on the request that wakes it, so those are retried.
        private static IEnumerator Send<TResponse>(Func<UnityWebRequest> create, Action<TResponse> done, Action<string, long> failed)
        {
            for (int attempt = 1; ; attempt++)
            {
                using (UnityWebRequest request = create())
                {
                    request.timeout = TimeoutSeconds;
                    yield return request.SendWebRequest();
                    if (attempt >= MaxAttempts || !MayBeAsleep(request))
                    {
                        Finish(request, done, failed);
                        yield break;
                    }
                }
                yield return new WaitForSeconds(RetryDelaySeconds);
            }
        }

        private static bool MayBeAsleep(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.ConnectionError || request.responseCode >= 500;
        }

        private static void Finish<TResponse>(UnityWebRequest request, Action<TResponse> done, Action<string, long> failed)
        {
            long status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : request.responseCode;
            if (request.result != UnityWebRequest.Result.Success)
            {
                failed(request.error, status);
                return;
            }
            // An error here would strand the coroutine waiting on this request, so it counts as a failed one.
            try
            {
                TResponse response = JsonConvert.DeserializeObject<TResponse>(request.downloadHandler.text);
                if (response == null)
                {
                    failed("Empty response", status);
                    return;
                }
                done(response);
            }
            catch (Exception e)
            {
                failed(e.Message, status);
            }
        }
    }
}
