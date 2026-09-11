module FSharp.Data.YamlValue.Tests.YamlReaderTests

open Xunit
open FSharp.Data
open FSharp.Data.YamlReader

[<Fact>]
let ``cursor tracks line and column across advances`` () =
    let c = Cursor("ab\ncd")
    Assert.Equal(1, c.Line)
    Assert.Equal(1, c.Column)
    c.Advance() // 'a'
    Assert.Equal(1, c.Line)
    Assert.Equal(2, c.Column)
    c.Advance() // 'b'
    Assert.Equal(1, c.Line)
    Assert.Equal(3, c.Column)
    c.Advance() // '\n'
    Assert.Equal(2, c.Line)
    Assert.Equal(1, c.Column)
    c.Advance() // 'c'
    Assert.Equal(2, c.Line)
    Assert.Equal(2, c.Column)

[<Fact>]
let ``crlf counts as a single line break`` () =
    let c = Cursor("a\r\nb")
    c.Advance() // 'a'
    Assert.Equal(1, c.Line)
    c.Advance() // '\r\n' together
    Assert.Equal(2, c.Line)
    Assert.Equal(1, c.Column)
    match c.Peek() with
    | Some 'b' -> ()
    | other -> Assert.True(false, sprintf "expected 'b', got %A" other)

[<Fact>]
let ``IsEof reflects end of source`` () =
    let c = Cursor("a")
    Assert.False(c.IsEof)
    c.Advance()
    Assert.True(c.IsEof)
    Assert.Equal(None, c.Peek())

[<Fact>]
let ``PeekAt looks ahead without consuming`` () =
    let c = Cursor("abc")
    Assert.Equal(Some 'a', c.PeekAt 0)
    Assert.Equal(Some 'b', c.PeekAt 1)
    Assert.Equal(Some 'c', c.PeekAt 2)
    Assert.Equal(None, c.PeekAt 3)
    // no mutation
    Assert.Equal(0, c.Offset)

[<Fact>]
let ``Matches checks a literal at the current position`` () =
    let c = Cursor("---\nfoo")
    Assert.True(c.Matches "---")
    Assert.False(c.Matches "----")
    Assert.False(c.Matches "foo")

[<Fact>]
let ``Position and Seek round-trip`` () =
    let c = Cursor("hello\nworld")
    c.Advance(7)
    let pos = c.Position
    c.Advance(3)
    Assert.NotEqual(pos.Offset, c.Offset)
    c.Seek(pos)
    Assert.Equal(pos.Offset, c.Offset)
    Assert.Equal(pos.Line, c.Line)
    Assert.Equal(pos.Column, c.Column)

[<Fact>]
let ``CountIndent counts leading spaces without consuming them`` () =
    let c = Cursor("    foo")
    let spaces, tabError = c.CountIndent()
    Assert.Equal(4, spaces)
    Assert.Equal(None, tabError)
    Assert.Equal(0, c.Offset) // unconsumed

[<Fact>]
let ``CountIndent reports a tab as illegal indentation`` () =
    let c = Cursor("  \tfoo")
    let spaces, tabError = c.CountIndent()
    Assert.Equal(2, spaces)
    Assert.Equal(Some 2, tabError)

[<Fact>]
let ``IndentAtLeast compares against a reference level`` () =
    let c = Cursor("    foo")
    Assert.True(c.IndentAtLeast 0)
    Assert.True(c.IndentAtLeast 4)
    Assert.False(c.IndentAtLeast 5)

[<Fact>]
let ``SkipBlanks consumes spaces and tabs only`` () =
    let c = Cursor("  \t  foo")
    let n = c.SkipBlanks()
    Assert.Equal(5, n)
    Assert.Equal(Some 'f', c.Peek())

[<Fact>]
let ``IsAtCommentStart requires preceding whitespace or start of line`` () =
    let atStart = Cursor("# comment")
    Assert.True(atStart.IsAtCommentStart true)

    let midScalar = Cursor("abc#def")
    midScalar.Advance(3)
    // '#' immediately follows 'c' with no whitespace before it - caller passes false.
    Assert.False(midScalar.IsAtCommentStart false)

    let afterSpace = Cursor("abc #def")
    afterSpace.Advance(4)
    Assert.True(afterSpace.IsAtCommentStart true)

[<Fact>]
let ``SkipComment consumes through end of line but not the line break`` () =
    let c = Cursor("# a comment\nnext")
    let text = c.SkipComment()
    Assert.Equal(" a comment", text)
    Assert.Equal(Some '\n', c.Peek())

[<Fact>]
let ``SkipComment at EOF consumes to the end`` () =
    let c = Cursor("# trailing")
    let text = c.SkipComment()
    Assert.Equal(" trailing", text)
    Assert.True(c.IsEof)

[<Fact>]
let ``Error builds a YamlParseException positioned at the cursor`` () =
    let c = Cursor("bad: [1, 2")
    c.Advance(5)
    let ex = c.Error("something went wrong")
    Assert.Equal(1, ex.Line)
    Assert.Equal(6, ex.Column)
    Assert.Contains("something went wrong", ex.Message)
