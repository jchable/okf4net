# C# Container

* [Shapes.Hidden](hidden.md) - Hidden, a code container in N.Shapes.

# C# Type

* [Shapes.Boxed](boxed.md) - A type whose header spans three lines, which nothing else here does. The header cap (R48) is the churn bound the whole id scheme rests on, and with every fixture type putting { on the next line it would produce a byte-identical golden if it simply returned StartLine + 1. This declaration is what makes that substitution visible.
* [Shapes.Corner](corner.md) - An enum, whose members are deliberately NOT emitted as concepts of their own.
* [Shapes.Holder](holder.md) - Non-generic, and the first of three declarations that differ only by arity.
* [Shapes.Holder_1](holder_1.md) - Arity one. A separate concept from Holder, which it was not before.
* [Shapes.Holder_2](holder_2.md) - Arity two.
* [Shapes.IShape](i-shape.md) - A contract with one member, so an interface_declaration reaches the golden.
* [Shapes.Point](point.md) - A struct, so the value-type arm of the declaration query is exercised.
* [Shapes.Size](size.md) - A positional record, whose header carries no body at all.

# Subdirectories

* [boxed](boxed/index.md) - Contains 5: Boxed.Boxed, builder, Boxed.Builder, Boxed.Changed, Boxed.Render.
* [hidden](hidden/index.md) - Not in scope by default.
* [holder](holder/index.md) - Held count.
* [holder_1](holder_1/index.md) - The held value.
* [holder_2](holder_2/index.md) - Looks the value up.
* [i-shape](i-shape/index.md) - Renders the shape.
* [point](point/index.md) - Contains 2: Point.X, Point.Y.
