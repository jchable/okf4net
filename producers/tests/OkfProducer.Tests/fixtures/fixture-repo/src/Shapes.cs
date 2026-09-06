// Declaration shapes the golden did not cover. Before this file, the captured bundle held three
// classes and their methods and nothing else: no interface, struct, record or enum, no constructor,
// event, field or local function, no nested or generic type, no explicit interface implementation,
// no block-scoped namespace, no non-public visibility, and no type whose header spans more than one
// line. Deleting the whole escaping layer, or returning `StartLine + 1` from the header cap, would
// not have moved one golden byte.
namespace N.Shapes
{
    /// <summary>A contract with one member, so an <c>interface_declaration</c> reaches the golden.</summary>
    public interface IShape
    {
        /// <summary>Renders the shape.</summary>
        string Render();
    }

    /// <summary>An enum, whose members are deliberately NOT emitted as concepts of their own.</summary>
    public enum Corner
    {
        Sharp,
        Round,
    }

    /// <summary>A struct, so the value-type arm of the declaration query is exercised.</summary>
    public struct Point
    {
        /// <summary>Two declarators on one field, which is the rule no fixture reached.</summary>
        public int X, Y;
    }

    /// <summary>A positional record, whose header carries no body at all.</summary>
    public record Size(int Width, int Height);

    /// <summary>
    /// A type whose header spans three lines, which nothing else here does. The header cap (R48) is
    /// the churn bound the whole id scheme rests on, and with every fixture type putting <c>{</c> on
    /// the next line it would produce a byte-identical golden if it simply returned
    /// <c>StartLine + 1</c>. This declaration is what makes that substitution visible.
    /// </summary>
    public sealed class Boxed
        : IShape,
          System.IEquatable<Boxed>
    {
        /// <summary>Raised when the box changes. An <c>event_field_declaration</c>.</summary>
        public event System.Action? Changed;

        private readonly Corner _corner;

        /// <summary>A constructor, which is its own declaration kind.</summary>
        public Boxed(Corner corner)
        {
            _corner = corner;
        }

        /// <summary>
        /// Renders the box. The doc comment carries <see cref="Corner"/> and markdown-significant
        /// characters -- [brackets], a `backtick` and a &lt;tag&gt; -- so the neutralisation layer is
        /// exercised by the golden rather than only by unit tests.
        /// </summary>
        public string Render()
        {
            string Compose(string prefix) => prefix + _corner;

            return Compose("box:");
        }

        /// <summary>Compares two boxes. Explicitly implemented, so it is a distinct concept from any public <c>Equals</c>.</summary>
        bool System.IEquatable<Boxed>.Equals(Boxed? other)
        {
            return other is not null && other._corner == _corner;
        }

        /// <summary>A nested type, so the containment spine has a type inside a type to describe.</summary>
        public sealed class Builder
        {
            /// <summary>Builds a box.</summary>
            public Boxed Build(Corner corner)
            {
                return new Boxed(corner);
            }
        }
    }

    /// <summary>Non-generic, and the first of three declarations that differ only by arity.</summary>
    public class Holder
    {
        /// <summary>Held count.</summary>
        public int Count()
        {
            return 0;
        }
    }

    /// <summary>Arity one. A separate concept from <see cref="Holder"/>, which it was not before.</summary>
    public class Holder<T>
    {
        /// <summary>The held value.</summary>
        public T? Value { get; set; }
    }

    /// <summary>Arity two.</summary>
    public class Holder<TKey, TValue>
    {
        /// <summary>Looks the value up.</summary>
        public TValue? Find(TKey key)
        {
            return default;
        }
    }

    /// <summary>Internal rather than public, so scope filtering has something to filter here.</summary>
    internal class Hidden
    {
        /// <summary>Not in scope by default.</summary>
        public void Never()
        {
        }
    }
}
