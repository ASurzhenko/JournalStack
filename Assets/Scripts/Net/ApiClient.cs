using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace JournalStack.Client
{
    public sealed class RegisterResult
    {
        public Reply Reply;
        public DeviceReply Device;
    }

    // Transport only. It never retries, never writes a file and never logs a token, a body or entry text.
    public sealed class ApiClient
    {
        private const int TokenLength = 43;
        private const int TimeoutSeconds = 10;

        private readonly string _baseUrl;

        public ApiClient(string baseUrl)
        {
            _baseUrl = baseUrl;
        }

        public async Task<Reply> SendAsync(string method, string path, string jsonBody, string token)
        {
            using (var request = new UnityWebRequest(_baseUrl + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = TimeoutSeconds;
                if (!string.IsNullOrEmpty(token))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                }

                if (jsonBody != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                var done = new TaskCompletionSource<bool>();
                request.SendWebRequest().completed += _ => done.TrySetResult(true);
                await done.Task;

                var reply = new Reply
                {
                    Status = (int)request.responseCode,
                    Body = request.downloadHandler.text,
                    NetworkError = request.result == UnityWebRequest.Result.ConnectionError
                        || request.result == UnityWebRequest.Result.DataProcessingError,
                };
                Debug.Log($"{nameof(ApiClient)}.{nameof(SendAsync)} [Net] {method} {path} -> {(reply.NetworkError ? "no answer" : reply.Status.ToString())}");
                return reply;
            }
        }

        public async Task<RegisterResult> RegisterAsync()
        {
            Reply reply = await SendAsync("POST", "/api/v1/devices", null, null);
            var result = new RegisterResult { Reply = reply };

            // A 2xx with the wrong shape is a failed registration, not an empty success.
            if (!reply.NetworkError
                && reply.Status == 201
                && Json.TryParse(reply.Body, "token", out DeviceReply device)
                && !string.IsNullOrEmpty(device.deviceId)
                && device.token != null
                && device.token.Length == TokenLength)
            {
                result.Device = device;
            }

            return result;
        }
    }
}
