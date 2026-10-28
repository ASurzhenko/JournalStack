using UnityEngine;

namespace JournalStack.Client
{
    [CreateAssetMenu(menuName = "JournalStack/Server Config")]
    public sealed class ServerConfig : ScriptableObject
    {
        [SerializeField] private string _editorUrl = "http://127.0.0.1:8080";
        [SerializeField] private string _deviceUrl = "https://example.invalid";

        // A phone cannot reach 127.0.0.1, so the Editor and a device use different addresses.
        public string BaseUrl => (Application.isEditor ? _editorUrl : _deviceUrl).TrimEnd('/');
    }
}
