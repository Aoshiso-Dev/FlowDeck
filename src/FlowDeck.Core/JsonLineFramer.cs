using System.Text;

namespace FlowDeck.Core;

public sealed class JsonLineFramer(int maxBytes)
{
    private readonly byte[] buffer = new byte[maxBytes];
    private int count;
    private bool discard;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public IEnumerable<string> Feed(byte[] data, int length)
    {
        var frames = new List<string>();
        for (var i = 0; i < length; i++)
        {
            var b = data[i];
            if (b == (byte)'\n')
            {
                if (!discard && count < maxBytes)
                {
                    try { frames.Add(StrictUtf8.GetString(buffer, 0, count).TrimEnd('\r')); }
                    catch (DecoderFallbackException) { }
                }
                count = 0; discard = false;
            }
            else if (!discard)
            {
                if (count >= maxBytes - 1) { discard = true; count = 0; }
                else buffer[count++] = b;
            }
        }
        return frames;
    }
}
