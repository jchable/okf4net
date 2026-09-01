// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.CodeGraph.TreeSitter;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;

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
