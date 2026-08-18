using System.Collections.Generic;

namespace Nox.FFmpeg.Base {
	public class Flux {
        public StreamType Type = StreamType.Av;
        public string Url = string.Empty;
        public Dictionary<string, string> Headers = new();

        public Flux(StreamType type, string url) {
            Type = type;
            Url = url;
        }

        public Flux(StreamType type, string url,  Dictionary<string, string> headers) {
            Type = type;
            Url = url;
            Headers = headers ?? new();
        }

        public static implicit operator Flux(string url)
            => new(StreamType.Av, url);
    }

    public static class FluxExtension {
        public static bool TryGet(this Flux[] flux, StreamType type,  out Flux o) {
            foreach (var f in flux) 
                if (f.Type == type) {
                    o = f;
                    return true;
                }
            o = null;
            return false;
        }
    }
}

