using UnityEngine;

namespace JournalStack.Client
{
    public enum ServerTarget
    {
        Local,
        Hosted,
    }

    [CreateAssetMenu(menuName = "JournalStack/Server Config")]
    public sealed class ServerConfig : ScriptableObject
    {
        [SerializeField] private ServerTarget _editorTarget = ServerTarget.Local;
        [SerializeField] private string _localUrl = "http://127.0.0.1:8080";
        [SerializeField] private string _hostedUrl = "https://example.invalid";

        // The Editor talks to the server picked in _editorTarget. A device build always talks to the hosted server,
        // because a phone cannot reach 127.0.0.1.
        public string BaseUrl => (Application.isEditor && _editorTarget == ServerTarget.Local ? _localUrl : _hostedUrl).TrimEnd('/');
    }
}
