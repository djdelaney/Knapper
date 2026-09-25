using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Knapper.Core.Query;

/// <summary>
/// The ONE frontmatter YAML deserializer, shared by frontmatter search and
/// lint. YamlDotNet's deserializer RECURSES once per nesting level and has no
/// depth limit, so a note of about 20 KB (<c>a: [[[[…]]]]</c>, 10,000 deep)
/// overflows the stack. .NET cannot catch a stack overflow: it kills the
/// PROCESS, and because lint indexes the whole vault, every later lint call
/// kills it again after systemd restarts it. Any agent can plant such a note
/// with one vault_create, and so can any synced device. The scanner and parser
/// underneath are iterative (a libyaml port with an explicit state stack), so
/// walking the event stream first and refusing excessive depth as an ordinary
/// <see cref="YamlException"/> keeps the recursive half from ever seeing it.
/// Callers already treat a YamlException as "could not examine".
/// </summary>
internal static class FrontmatterYaml
{
    /// <summary>
    /// Real frontmatter nests two or three levels deep; 64 leaves plenty of
    /// headroom while keeping deserializer recursion trivially small on any
    /// thread's stack.
    /// </summary>
    internal const int MaxDepth = 64;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <summary>
    /// Null for an empty document; throws <see cref="YamlException"/> — and
    /// ONLY YamlException — when malformed or nested too deeply.
    /// </summary>
    /// <remarks>
    /// "Only" is this method's job, not YamlDotNet's: its scanner throws
    /// <see cref="InvalidOperationException"/> for any unclosed <c>[</c> or
    /// <c>{</c> followed by another line (<c>status: [unclosed\ntags: a</c>) —
    /// ordinary hand-edit damage. Both callers catch YamlException as "could
    /// not examine this note", so the stray type escaped them and failed lint
    /// and frontmatter search for the whole vault with [Internal], from one
    /// note. Translating at the ONE parse point, and by exclusion rather than
    /// by listing the types seen so far, is what keeps a type nobody has met
    /// yet from doing the same (<c>FrontmatterParseFailureTests</c> fuzzes it).
    /// Out-of-memory is not a property of the note and is left alone.
    /// </remarks>
    public static Dictionary<string, object?>? Deserialize(string block)
    {
        try
        {
            RequireBoundedDepth(block);
            return Deserializer.Deserialize<Dictionary<string, object?>>(block);
        }
        catch (Exception e) when (e is not (YamlException or OutOfMemoryException))
        {
            // The inner exception's type, never the block: frontmatter is note
            // content and must not ride an exception into a log.
            throw new YamlException(Mark.Empty, Mark.Empty,
                $"frontmatter could not be parsed ({e.GetType().Name})", e);
        }
    }

    private static void RequireBoundedDepth(string block)
    {
        var parser = new Parser(new StringReader(block));
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart or SequenceStart:
                    if (++depth > MaxDepth)
                    {
                        throw new YamlException(parser.Current.Start, parser.Current.End,
                            $"frontmatter nests deeper than {MaxDepth} levels");
                    }
                    break;
                case MappingEnd or SequenceEnd:
                    depth--;
                    break;
            }
        }
    }
}
