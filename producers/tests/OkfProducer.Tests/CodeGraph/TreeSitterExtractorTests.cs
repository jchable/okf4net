// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.CodeGraph.TreeSitter;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.CodeGraph;

public class TreeSitterExtractorTests : IDisposable
{
    private readonly TreeSitterExtractor _extractor = new();
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        _extractor.Dispose();

        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked file on the way out should not fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Offsets_survive_a_non_ascii_identifier_before_the_call()
    {
        // §2.1, the bug class this whole offset discipline exists to prevent:
        // tree-sitter counts bytes, Roslyn counts UTF-16. "café" is 5 bytes and
        // 4 chars, so every offset after it differs by one.
        const string source = "var café = Foo();";

        var utf8 = source.IndexOf("Foo", StringComparison.Ordinal);   // UTF-16 index
        Assert.NotEqual(Utf8Offsets.ToUtf8(source, utf8), utf8);
        Assert.Equal(utf8, Utf8Offsets.ToUtf16(source, Utf8Offsets.ToUtf8(source, utf8)));
    }

    [Theory]
    [InlineData("var x = \"🎯\"; Foo();")]     // astral plane, surrogate pair
    [InlineData("var naïve = 1;\r\nFoo();")]   // CRLF
    [InlineData("// commentaire accentué\nFoo();")]
    public void Offset_conversion_round_trips(string source)
    {
        var utf16 = source.IndexOf("Foo", StringComparison.Ordinal);

        Assert.Equal(utf16, Utf8Offsets.ToUtf16(source, Utf8Offsets.ToUtf8(source, utf16)));
    }

    // The guard above exercises Utf8Offsets in isolation; this one runs the same non-ASCII-before-a-
    // call shape through the REAL extractor and checks the CallSite it produces lands on the correct
    // UTF-8 byte offset. TreeSitter.DotNet 1.3.0 turned out not to report raw tree-sitter byte
    // offsets at all -- see this test's own "café" case below, and the task report, for the full
    // finding -- so this is the test that would have caught a wrong conversion (or a skipped one)
    // inside TreeSitterExtractor itself.
    [Theory]
    [InlineData("namespace N;\npublic class T { public void M() { var café = 1; Foo(); } }")]
    [InlineData("namespace N;\npublic class T { public void M() { var x = \"🎯\"; Foo(); } }")]
    [InlineData("namespace N;\npublic class T { public void M() { var naïve = 1;\r\nFoo(); } }")]
    public void Offsets_survive_a_non_ascii_identifier_before_the_call_through_the_real_extractor(string source)
    {
        var result = ExtractSource(source);

        var site = Assert.Single(result.Sites);
        var expectedUtf16 = source.IndexOf("Foo", StringComparison.Ordinal);
        var expectedUtf8 = Utf8Offsets.ToUtf8(source, expectedUtf16);

        Assert.Equal(expectedUtf8, site.Offset);
        Assert.NotEqual(expectedUtf16, site.Offset);
    }

    [Fact]
    public void Public_types_and_members_are_extracted_with_their_doc_comment()
    {
        var result = ExtractSource("""
            namespace N;
            /// <summary>Scans a body.</summary>
            public sealed class Scanner
            {
                /// <summary>Scans it.</summary>
                public int Scan(string body) => body.Length;
                private int Hidden() => 0;
            }
            """);

        var type = Assert.Single(result.Symbols, s => s.Kind == SymbolKind.Type);
        Assert.Equal("Scanner", type.Name);
        Assert.Equal("Scans a body.", type.DocComment);

        var member = Assert.Single(result.Symbols, s => s.Kind == SymbolKind.Member && s.Visibility == SymbolVisibility.Public);
        Assert.Equal("Scan", member.Name);
        Assert.Equal("Scans it.", member.DocComment);
    }

    [Fact]
    public void Private_members_are_extracted_but_marked_so_scope_can_filter_them()
        => Assert.Contains(
            ExtractSource("namespace N;\npublic class T { private int Hidden() => 0; }").Symbols,
            s => s.Name == "Hidden" && s.Visibility == SymbolVisibility.Private);

    [Fact]
    public void Local_functions_are_covered()
    {
        // The spike's remaining 1.2% attachment gap was local_function_statement.
        var result = ExtractSource("namespace N;\npublic class T { public void M() { void Inner() { } Inner(); } }");

        Assert.Contains(result.Sites, s => s.CalledName == "Inner");
    }

    [Fact]
    public void Call_sites_carry_the_enclosing_symbol()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { Other(); } }");

        var site = Assert.Single(result.Sites);
        // CallerContainer is the fully-qualified path (matching M's own SymbolFact.Container),
        // not the bare type name "T": Task 8 joins a call site back to its caller's own concept
        // by (Container, Name), and a bare type name is ambiguous the moment two namespaces each
        // hold a type named T -- see CallerContainer_joins_exactly_to_its_caller_symbol_even_with_
        // same_named_types_across_namespaces below, which pins the join itself.
        Assert.Equal("N.T", site.CallerContainer);
        Assert.Equal("M", site.CallerName);
        Assert.Equal("Other", site.CalledName);
    }

    [Fact]
    public void CallerContainer_joins_exactly_to_its_caller_symbol_even_with_same_named_types_across_namespaces()
    {
        var result = ExtractSource("""
            namespace N1
            {
                public class T
                {
                    public void M() { Foo(); }
                }
            }
            namespace N2
            {
                public class T
                {
                    public void M() { Bar(); }
                }
            }
            """);

        var fooSite = Assert.Single(result.Sites, s => s.CalledName == "Foo");
        var barSite = Assert.Single(result.Sites, s => s.CalledName == "Bar");

        Assert.Equal("N1.T", fooSite.CallerContainer);
        Assert.Equal("N2.T", barSite.CallerContainer);

        // The actual join Task 8 performs: match (Container, Name) against every SymbolFact.
        // Each call site must resolve to exactly one caller, and it must be the right one.
        var fooCaller = Assert.Single(result.Symbols, s => s.Container == fooSite.CallerContainer && s.Name == fooSite.CallerName);
        var barCaller = Assert.Single(result.Symbols, s => s.Container == barSite.CallerContainer && s.Name == barSite.CallerName);

        Assert.Equal("N1.T", fooCaller.Container);
        Assert.Equal("N2.T", barCaller.Container);
    }

    [Fact]
    public void A_call_in_a_field_or_event_field_initializer_gets_its_declarator_as_the_caller()
    {
        var result = ExtractSource("""
            namespace N;
            public class T
            {
                public int F = Compute();
                public event System.Action E = MakeHandler();
            }
            """);

        var fieldSite = Assert.Single(result.Sites, s => s.CalledName == "Compute");
        Assert.Equal("N.T", fieldSite.CallerContainer);
        Assert.Equal("F", fieldSite.CallerName);

        var eventSite = Assert.Single(result.Sites, s => s.CalledName == "MakeHandler");
        Assert.Equal("N.T", eventSite.CallerContainer);
        Assert.Equal("E", eventSite.CallerName);
    }

    [Fact]
    public void A_call_in_a_multi_declarator_field_initializer_attributes_to_the_right_declarator()
    {
        var result = ExtractSource("namespace N;\npublic class T { public int a = Foo(), b = Bar(); }");

        var fooSite = Assert.Single(result.Sites, s => s.CalledName == "Foo");
        var barSite = Assert.Single(result.Sites, s => s.CalledName == "Bar");

        Assert.Equal("a", fooSite.CallerName);
        Assert.Equal("b", barSite.CallerName);
        Assert.Equal(fooSite.CallerContainer, barSite.CallerContainer);
    }

    [Fact]
    public void A_type_with_no_modifier_defaults_to_internal_visibility()
    {
        var result = ExtractSource("namespace N;\nclass Plain {}");

        var type = Assert.Single(result.Symbols);
        Assert.Equal(SymbolVisibility.Internal, type.Visibility);
    }

    [Fact]
    public void A_member_with_no_modifier_defaults_to_private_visibility()
    {
        var result = ExtractSource("namespace N;\npublic class T { void M() {} }");

        var member = Assert.Single(result.Symbols, s => s.Kind == SymbolKind.Member);
        Assert.Equal(SymbolVisibility.Private, member.Visibility);
    }

    [Fact]
    public void An_interface_member_with_no_modifier_is_implicitly_public()
    {
        var result = ExtractSource("namespace N;\npublic interface IThing { void M(); }");

        var member = Assert.Single(result.Symbols, s => s.Kind == SymbolKind.Member);
        Assert.Equal(SymbolVisibility.Public, member.Visibility);
    }

    [Fact]
    public void Protected_internal_is_public_and_private_protected_is_private()
    {
        var result = ExtractSource("""
            namespace N;
            public class T
            {
                protected internal void A() {}
                private protected void B() {}
            }
            """);

        Assert.Equal(SymbolVisibility.Public, Assert.Single(result.Symbols, s => s.Name == "A").Visibility);
        Assert.Equal(SymbolVisibility.Private, Assert.Single(result.Symbols, s => s.Name == "B").Visibility);
    }

    [Fact]
    public void A_nested_namespace_and_type_become_a_dotted_container()
    {
        var result = ExtractSource("namespace N.Sub;\npublic class Outer { public class Inner { public void M() {} } }");

        var member = Assert.Single(result.Symbols, s => s.Name == "M");
        Assert.Equal("N.Sub.Outer.Inner", member.Container);
    }

    [Fact]
    public void A_block_namespace_also_produces_a_dotted_container()
    {
        var result = ExtractSource("namespace N.Sub { public class T { public void M() {} } }");

        var member = Assert.Single(result.Symbols, s => s.Name == "M");
        Assert.Equal("N.Sub.T", member.Container);
    }

    [Fact]
    public void A_local_function_s_container_includes_its_enclosing_method()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { void Inner() {} } }");

        var local = Assert.Single(result.Symbols, s => s.Name == "Inner");
        Assert.Equal("N.T.M", local.Container);
    }

    [Fact]
    public void An_enum_yields_its_type_symbol_but_no_member_symbols()
    {
        // Enum members are public API with no modifier syntax to hang a visibility default off of;
        // rather than invent one, the enum type is the concept and its members are not extracted as
        // symbols in their own right (a later task lists them in the enum concept's own body).
        var result = ExtractSource("namespace N;\npublic enum EThing { A, B, C }");

        var type = Assert.Single(result.Symbols);
        Assert.Equal(SymbolKind.Type, type.Kind);
        Assert.Equal("EThing", type.Name);
        Assert.DoesNotContain(result.Symbols, s => s.Kind == SymbolKind.Member);
    }

    [Fact]
    public void A_multi_declarator_field_yields_one_symbol_per_name_sharing_the_same_span()
    {
        var result = ExtractSource("namespace N;\npublic class T { public int a, b; }");

        var a = Assert.Single(result.Symbols, s => s.Name == "a");
        var b = Assert.Single(result.Symbols, s => s.Name == "b");
        Assert.Equal(a.StartOffset, b.StartOffset);
        Assert.Equal(a.EndOffset, b.EndOffset);
        Assert.Equal(SymbolVisibility.Public, a.Visibility);
        Assert.Equal(SymbolVisibility.Public, b.Visibility);
    }

    // A property has no `body` field at all -- `accessors` and/or `value` carry its accessor list,
    // its arrow implementation, or its initializer, and all of them must be excluded from Signature
    // the same way a method's block is (Task 8 emits this string into every member concept's
    // `## Signatures` section). Covers block- and arrow-bodied methods (already correct before this
    // fix), an auto-property, an arrow-bodied property, an auto-property with an initializer (where
    // `accessors` and `value` are both present -- `accessors` must win, since it starts first), and a
    // field (no body/accessors/value field at all -- the pre-existing, still-correct fallback).
    [Theory]
    [InlineData("namespace N;\npublic class T { public int M(int x) { return x; } }", "M", "public int M(int x)")]
    [InlineData("namespace N;\npublic class T { public int M() => 42; }", "M", "public int M()")]
    [InlineData("namespace N;\npublic class T { public int P { get; set; } }", "P", "public int P")]
    [InlineData("namespace N;\npublic class T { public int Q => 42; }", "Q", "public int Q")]
    [InlineData("namespace N;\npublic class T { public int R { get; set; } = 5; }", "R", "public int R")]
    [InlineData("namespace N;\npublic class T { public int F; }", "F", "public int F")]
    public void Signature_excludes_the_body_accessors_or_initializer_for_every_member_shape(
        string source, string name, string expectedSignature)
    {
        var result = ExtractSource(source);

        var member = Assert.Single(result.Symbols, s => s.Name == name);
        Assert.Equal(expectedSignature, member.Signature);
    }

    [Fact]
    public void Calls_through_a_qualifier_generic_or_null_conditional_are_all_captured_by_simple_name()
    {
        var result = ExtractSource("""
            namespace N;
            public class T
            {
                public void M(T obj)
                {
                    Bare();
                    obj.Qualified();
                    Generic<int>();
                    obj.QualifiedGeneric<int>();
                    obj?.NullConditional();
                }
            }
            """);

        Assert.Equal(
            new[] { "Bare", "Generic", "NullConditional", "Qualified", "QualifiedGeneric" },
            result.Sites.Select(s => s.CalledName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_multi_line_doc_comment_joins_its_summary_across_lines()
    {
        var result = ExtractSource("""
            namespace N;
            /// <summary>
            /// Scans a body,
            /// across lines.
            /// </summary>
            public sealed class Scanner {}
            """);

        var type = Assert.Single(result.Symbols);
        Assert.Equal("Scans a body, across lines.", type.DocComment);
    }

    [Fact]
    public void A_plain_comment_run_does_not_count_as_a_doc_comment()
    {
        var result = ExtractSource("""
            namespace N;
            // just a remark, not a doc comment
            public sealed class Scanner {}
            """);

        var type = Assert.Single(result.Symbols);
        Assert.Null(type.DocComment);
    }

    [Fact]
    public void A_type_with_no_leading_comment_has_no_doc_comment()
    {
        var result = ExtractSource("namespace N;\npublic sealed class Scanner {}");

        Assert.Null(Assert.Single(result.Symbols).DocComment);
    }

    [Fact]
    public void All_symbols_and_sites_carry_the_relative_path_and_language()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { Other(); } }", relativePath: "src/T.cs");

        var symbol = Assert.Single(result.Symbols, s => s.Name == "M");
        Assert.Equal("src/T.cs", symbol.RelativePath);
        Assert.Equal("csharp", symbol.Language);

        var site = Assert.Single(result.Sites);
        Assert.Equal("src/T.cs", site.RelativePath);
    }

    [Fact]
    public void A_declarations_header_end_line_is_the_line_its_body_opens_on()
    {
        // What SymbolFact.HeaderEndLine is for: a type declaration's own span runs to its CLOSING
        // brace, so without a separate header line every edit inside the body would move the type's
        // rendered span and rewrite its concept -- and §8.3 promises adding a private member changes
        // no concept at all.
        //
        // Both halves are pinned. The type's header stops on the brace line (2, not 5), and the
        // member's stops on its own brace line (4) -- the member's FULL span is untouched, which is
        // what emission still renders for it.
        var result = ExtractSource("""
            namespace N;
            public class T
            {
                public void M()
                {
                }
            }
            """);

        var type = result.Symbols.Single(s => s.Kind == SymbolKind.Type);
        var member = result.Symbols.Single(s => s.Kind == SymbolKind.Member);

        Assert.Equal(2, type.StartLine);
        Assert.Equal(7, type.EndLine);
        Assert.Equal(3, type.HeaderEndLine);

        Assert.Equal(4, member.StartLine);
        Assert.Equal(6, member.EndLine);
        Assert.Equal(5, member.HeaderEndLine);
    }

    [Fact]
    public void An_enums_header_end_line_is_the_line_its_member_list_opens_on()
    {
        // Checked because it is not obvious from the query: `enum_declaration` captures its members in
        // an `enum_member_declaration_list`, and whether the grammar exposes that under the `body`
        // FIELD -- rather than as an unnamed child -- is what decides whether HeaderEndNode finds it.
        // If it did not, an enum would silently fall back to its full end line and enum concepts would
        // keep the exact churn defect the cap exists to remove, one shape over and invisible in a
        // golden with no enum in it.
        var result = ExtractSource("""
            namespace N;
            public enum Colour
            {
                Red,
                Green,
            }
            """);

        var enumType = result.Symbols.Single(s => s.Name == "Colour");

        Assert.Equal(SymbolKind.Type, enumType.Kind);
        Assert.Equal(2, enumType.StartLine);
        Assert.Equal(6, enumType.EndLine);
        Assert.Equal(3, enumType.HeaderEndLine);
    }

    [Fact]
    public void A_declaration_with_no_body_reports_its_own_last_line_as_its_header_end()
    {
        // The other branch: a field has no `body`, `accessors` or `value` node to stop at, so there is
        // no brace line and the declaration IS its header. Reporting null here instead would make
        // emission silently fall back to EndLine -- the same answer by accident rather than by rule.
        var result = ExtractSource("namespace N;\npublic class T { public int Field; }");

        var field = result.Symbols.Single(s => s.Name == "Field");

        Assert.Equal(field.EndLine, field.HeaderEndLine);
    }

    [Fact]
    public void Extraction_is_marked_complete()
    {
        var result = ExtractSource("namespace N;\npublic class T {}");

        Assert.Equal(FileStatus.Extracted, result.Status);
    }

    [Fact]
    public void Repeated_extraction_of_the_same_source_produces_the_same_order()
    {
        const string source = """
            namespace N;
            public class T
            {
                public void B() { Z(); }
                public void A() { Y(); }
            }
            """;

        var first = ExtractSource(source);
        var second = ExtractSource(source);

        Assert.Equal(first.Symbols.Select(s => s.Name), second.Symbols.Select(s => s.Name));
        Assert.Equal(first.Sites.Select(s => s.CalledName), second.Sites.Select(s => s.CalledName));
    }

    [Fact]
    public void Generic_types_of_different_arity_are_distinct_symbols()
    {
        // `Foo`, `Foo<T>` and `Foo<T, U>` all report `name` as `Foo`, so they used to collapse into one
        // symbol group and render as overloads of a single member -- §3.2's merge rule, written for
        // method overloads, silently extended to unrelated types.
        //
        // The separator was `_N` first, chosen so "arity 1" would not read as the registry's "second
        // thing called Foo" (`-2`). It is a backtick now, because legibility was the wrong thing to
        // optimise: `_` and a digit are both C# identifier characters, so the qualification was NOT
        // injective and a real `Foo_1` collapsed back together with `Foo<T>` -- see the sibling test
        // below. A backtick cannot occur in a C# identifier under any spelling, and it is the CLR's own
        // arity convention.
        var result = ExtractSource("""
            namespace N;
            public class Foo { }
            public class Foo<T> { }
            public class Foo<T, U> { }
            """);

        Assert.Equal(
            ["Foo", "Foo`1", "Foo`2"],
            result.Symbols.Where(s => s.Kind == SymbolKind.Type).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_type_named_like_an_arity_suffix_stays_distinct_from_the_generic_it_would_have_collided_with()
    {
        // The injectivity the separator exists for, and the defect that changed it. Under `_N`, a type
        // genuinely named `Holder_1` and a type named `Holder<T>` both produced the SymbolFact.Name
        // "Holder_1": one SymbolKey group, one concept listing both `public class Holder_1` and
        // `public class Holder<T>`, one description taken from whichever sorted first, and the members
        // of two unrelated types shown as siblings under it. That is exactly the merge the arity rule
        // was added to remove, reachable from perfectly legal C#.
        //
        // Asserted on the extractor, where the qualification happens, rather than on the emitted ids:
        // `CodeConceptIds` deliberately does not treat `_` as a word boundary, so under the old rule
        // the two also slugified identically and the id level could not tell them apart either.
        var result = ExtractSource("""
            namespace N;
            public class Holder_1 { }
            public class Holder<T> { }
            """);

        var names = result.Symbols.Where(s => s.Kind == SymbolKind.Type).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Holder_1", names);
        Assert.Contains("Holder`1", names);
    }

    [Fact]
    public void A_generic_method_keeps_its_bare_name_so_calls_still_match_it()
    {
        // The deliberate asymmetry, and the reason it is not an oversight. A call site captures its
        // callee as the bare identifier -- `Bar<int>()` yields `Bar` -- so suffixing a generic METHOD
        // would stop every call to it from matching by name. That trades a merged concept for a lost
        // edge, which is the worse of the two, so arity is a TYPE rule only.
        var result = ExtractSource("""
            namespace N;
            public class T { public void Bar<U>() { } }
            """);

        Assert.Contains("Bar", result.Symbols.Select(s => s.Name));
    }

    [Fact]
    public void An_explicit_interface_implementation_is_named_apart_from_the_public_member()
    {
        // Both report `name` as `Bar`, so at THIS layer the two were one symbol. The qualified form
        // takes the interface as a dotted prefix, which is how C# writes it.
        //
        // What that does NOT do, measured rather than assumed after the register claimed otherwise:
        // it does not split a merged CONCEPT, because there was never a merged concept to split. An
        // explicit interface implementation carries no access modifier, so `VisibilityOf` classes it
        // Private, and Private is out of scope under every flag -- `--include-internal` included. It is
        // filtered before ConceptGenerator ever groups anything. The register's D1b-I2 said the two
        // collapsed into "one concept, one description, both signatures"; that outcome is not
        // reachable through the shipped pipeline, and the golden confirms it -- the fixture's explicit
        // `IEquatable<Boxed>.Equals` produces no concept at all.
        //
        // The fix is still right, one layer down: two different members sharing one name is wrong for
        // any consumer reading SymbolFacts before the scope filter, and name matching would otherwise
        // bind a call to `Bar()` -- which cannot reach the explicit member on the type -- to it.
        var result = ExtractSource("""
            namespace N;
            public interface IFoo { void Bar(); }
            public class Impl : IFoo
            {
                public void Bar() { }
                void IFoo.Bar() { }
            }
            """);

        var names = result.Symbols.Where(s => s.Container == "N.Impl").Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(["Bar", "IFoo.Bar"], names);
    }

    [Fact]
    public void A_call_inside_an_indexer_or_an_operator_yields_no_edge_at_all()
    {
        // The documented gap: indexers, operator overloads and conversion operators are not extracted
        // as symbols, because none of them has a `name` field in this grammar (an indexer is written
        // `this[...]`; an operator's symbol is an anonymous child after the anonymous keyword). Naming
        // them would need bespoke, unverified rules, so the profile accepts the gap.
        //
        // What that gap RIPPLES into was never stated or tested: a call inside one of those members
        // finds no ancestor in CallerMemberAncestorNodeTypes, so the site comes out with an empty
        // caller. An edge naming a caller that does not exist is exactly what §2.1 calls worse than no
        // edge -- so what must be true is that NONE survives, and that is what this pins.
        //
        // It is not the extractor that drops it: the site is emitted with an empty caller and
        // CodeGraphBuilder's invariant (no edge may name a caller absent from Symbols) is what removes
        // it. Stated here because a future change to either half would silently let the orphan through.
        var result = ExtractSource(IndexerAndOperatorSource);

        // The gap itself, asserted so this test fails loudly rather than vacuously if the profile ever
        // starts extracting them -- at which point the ripple below stops being the right behaviour.
        Assert.DoesNotContain(result.Symbols, s => s.Name is "this" or "+" or "op_Addition");

        // Every call site found inside those two members carries no caller to hang a concept off.
        Assert.All(
            result.Sites.Where(s => s.CalledName == "Target"),
            site => Assert.True(
                site.CallerName.Length == 0,
                $"expected no caller for a call inside an indexer or operator, got '{site.CallerName}'."));

        // THE SECOND HALF, which this test named ("yields no edge at all") and did not make. Everything
        // above is computed from the extractor alone, so deleting `CodeGraphBuilder`'s invariant -- no
        // edge may name a caller absent from Symbols -- left the orphan edge in the graph with this
        // test still green, and `CSharpProfile`'s doc comment claimed it "pins both halves". Building
        // the graph is what turns that claim into an assertion, and it is cheap: the same source, the
        // real profile, no resolver, so nothing but the invariant can remove the edge.
        using var extractor = new TreeSitterExtractor();
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-indexer-").FullName;
        _tempDirectories.Add(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "T.cs"), IndexerAndOperatorSource);

        var graph = new CodeGraphBuilder(extractor, [CSharpProfile.Instance], [])
            .Build(new RepositorySnapshot(repoPath, "indexer-repo", [], []), ExtractionLimits.Default, ScopeOptions.Default);

        Assert.DoesNotContain(graph.Edges, e => e.Site.CallerName.Length == 0);
        Assert.Empty(graph.Edges);
    }

    /// <summary>
    /// The fixture for <c>A_call_inside_an_indexer_or_an_operator_yields_no_edge_at_all</c>, shared by
    /// its two halves so the extractor assertions and the graph assertion cannot drift onto different
    /// source and quietly stop being about the same calls.
    /// </summary>
    private const string IndexerAndOperatorSource = """
        namespace N;
        public class T
        {
            public void Target() { }

            public int this[int i] { get { Target(); return i; } }

            public static T operator +(T a, T b) { a.Target(); return a; }
        }
        """;

    private ExtractionResult ExtractSource(string source, string relativePath = "T.cs")
    {
        var directory = Directory.CreateTempSubdirectory("okfproducer-treesitter-").FullName;
        _tempDirectories.Add(directory);
        var absolutePath = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, source);

        return _extractor.Extract(relativePath, absolutePath, CSharpProfile.Instance, ExtractionLimits.Default);
    }
}
