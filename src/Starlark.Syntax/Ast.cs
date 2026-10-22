using System.Text;

namespace Starlark.Syntax;

public enum BinOp
{
    Or, And, Equal, NotEqual, Less, Greater, LessOrEqual, GreaterOrEqual, In, NotIn,
    Subtract, Add, Multiply, Percent, Divide, FloorDivide, BitAnd, BitOr, BitXor, LeftShift, RightShift,
}

public enum AssignOp
{
    Add, Subtract, Multiply, Divide, FloorDivide, Percent, BitAnd, BitOr, BitXor, LeftShift, RightShift,
}

public static class OpText
{
    /// <summary>Operator text with surrounding spaces, as printed in the AST.</summary>
    public static string Padded(this BinOp op) => $" {Symbol(op)} ";

    public static string Symbol(this BinOp op) => op switch
    {
        BinOp.Or => "or",
        BinOp.And => "and",
        BinOp.Equal => "==",
        BinOp.NotEqual => "!=",
        BinOp.Less => "<",
        BinOp.Greater => ">",
        BinOp.LessOrEqual => "<=",
        BinOp.GreaterOrEqual => ">=",
        BinOp.In => "in",
        BinOp.NotIn => "not in",
        BinOp.Subtract => "-",
        BinOp.Add => "+",
        BinOp.Multiply => "*",
        BinOp.Percent => "%",
        BinOp.Divide => "/",
        BinOp.FloorDivide => "//",
        BinOp.BitAnd => "&",
        BinOp.BitOr => "|",
        BinOp.BitXor => "^",
        BinOp.LeftShift => "<<",
        BinOp.RightShift => ">>",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    public static string Padded(this AssignOp op) => $" {Symbol(op)} ";

    public static string Symbol(this AssignOp op) => op switch
    {
        AssignOp.Add => "+=",
        AssignOp.Subtract => "-=",
        AssignOp.Multiply => "*=",
        AssignOp.Divide => "/=",
        AssignOp.FloorDivide => "//=",
        AssignOp.Percent => "%=",
        AssignOp.BitAnd => "&=",
        AssignOp.BitOr => "|=",
        AssignOp.BitXor => "^=",
        AssignOp.LeftShift => "<<=",
        AssignOp.RightShift => ">>=",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}

/// <summary>Base of every AST node: knows the source span it was parsed from.</summary>
public abstract class Node(Span span)
{
    public Span Span { get; } = span;
}

/// <summary>A node paired with a string value (identifier names, string literals, attribute names).</summary>
public sealed class AstString(string value, Span span) : Node(span)
{
    public string Value { get; } = value;
    public override string ToString() => Value;
}

// ======================================================================== expressions

public abstract class Expr(Span span) : Node(span)
{
    /// <summary>Call <paramref name="f"/> on each direct child expression.</summary>
    public abstract void VisitExpr(Action<Expr> f);

    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    internal abstract void Write(StringBuilder sb);

    internal void WritePostfixReceiver(StringBuilder sb)
    {
        if (this is MinusExpr or PlusExpr or BitNotExpr)
        {
            sb.Append('(');
            Write(sb);
            sb.Append(')');
        }
        else
        {
            Write(sb);
        }
    }

    internal static void WriteList(StringBuilder sb, IEnumerable<Expr> items, bool forTuple = false)
    {
        int n = 0;
        foreach (var e in items)
        {
            if (n++ > 0)
            {
                sb.Append(", ");
            }
            e.Write(sb);
        }
        if (n == 1 && forTuple)
        {
            sb.Append(',');
        }
    }

    internal static void WriteStringLiteral(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\0': sb.Append("\\x00"); break;
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}

public sealed class TupleExpr(IReadOnlyList<Expr> items, Span span) : Expr(span)
{
    public IReadOnlyList<Expr> Items { get; } = items;
    public override void VisitExpr(Action<Expr> f) { foreach (var x in Items) f(x); }
    internal override void Write(StringBuilder sb) { sb.Append('('); WriteList(sb, Items, forTuple: true); sb.Append(')'); }
}

public sealed class DotExpr(Expr target, AstString field, Span span) : Expr(span)
{
    public Expr Target { get; } = target;
    public AstString Field { get; } = field;
    public override void VisitExpr(Action<Expr> f) => f(Target);

    internal override void Write(StringBuilder sb)
    {
        if (Target is IntLiteral)
        {
            sb.Append('('); Target.Write(sb); sb.Append(").").Append(Field.Value);
        }
        else
        {
            Target.WritePostfixReceiver(sb); sb.Append('.').Append(Field.Value);
        }
    }
}

public sealed class CallExpr(Expr function, IReadOnlyList<Argument> args, Span span) : Expr(span)
{
    public Expr Function { get; } = function;
    public IReadOnlyList<Argument> Args { get; } = args;

    public override void VisitExpr(Action<Expr> f)
    {
        f(Function);
        foreach (var a in Args) f(a.Value);
    }

    internal override void Write(StringBuilder sb)
    {
        Function.WritePostfixReceiver(sb);
        sb.Append('(');
        for (int i = 0; i < Args.Count; i++)
        {
            if (i != 0) sb.Append(", ");
            Args[i].Write(sb);
        }
        sb.Append(')');
    }
}

public sealed class IndexExpr(Expr target, Expr index, Span span) : Expr(span)
{
    public Expr Target { get; } = target;
    public Expr Index { get; } = index;
    public override void VisitExpr(Action<Expr> f) { f(Target); f(Index); }
    internal override void Write(StringBuilder sb) { Target.WritePostfixReceiver(sb); sb.Append('['); Index.Write(sb); sb.Append(']'); }
}

/// <summary><c>a[i, j]</c></summary>
public sealed class Index2Expr(Expr target, Expr first, Expr second, Span span) : Expr(span)
{
    public Expr Target { get; } = target;
    public Expr First { get; } = first;
    public Expr Second { get; } = second;
    public override void VisitExpr(Action<Expr> f) { f(Target); f(First); f(Second); }

    internal override void Write(StringBuilder sb)
    {
        Target.WritePostfixReceiver(sb);
        sb.Append('['); First.Write(sb); sb.Append(", "); Second.Write(sb); sb.Append(']');
    }
}

public sealed class SliceExpr(Expr target, Expr? start, Expr? stop, Expr? step, Span span) : Expr(span)
{
    public Expr Target { get; } = target;
    public Expr? Start { get; } = start;
    public Expr? Stop { get; } = stop;
    public Expr? Step { get; } = step;

    public override void VisitExpr(Action<Expr> f)
    {
        f(Target);
        if (Start is not null) f(Start);
        if (Stop is not null) f(Stop);
        if (Step is not null) f(Step);
    }

    internal override void Write(StringBuilder sb)
    {
        Target.WritePostfixReceiver(sb);
        sb.Append('[');
        if (Start is not null) { Start.Write(sb); sb.Append(':'); } else { sb.Append(':'); }
        Stop?.Write(sb);
        if (Step is not null) { sb.Append(':'); Step.Write(sb); }
        sb.Append(']');
    }
}

/// <summary>An identifier in read position.</summary>
public sealed class IdentExpr(string name, Span span) : Expr(span)
{
    public string Name { get; } = name;
    public override void VisitExpr(Action<Expr> f) { }
    internal override void Write(StringBuilder sb) => sb.Append(Name);
}

public sealed class LambdaExpr(IReadOnlyList<Parameter> parameters, Expr body, Span span) : Expr(span)
{
    public IReadOnlyList<Parameter> Parameters { get; } = parameters;
    public Expr Body { get; } = body;

    public override void VisitExpr(Action<Expr> f)
    {
        foreach (var p in Parameters) p.VisitExpr(f);
        f(Body);
    }

    internal override void Write(StringBuilder sb)
    {
        sb.Append("(lambda ");
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (i != 0) sb.Append(", ");
            Parameters[i].Write(sb);
        }
        sb.Append(": ");
        Body.Write(sb);
        sb.Append(')');
    }
}

public abstract class LiteralExpr(Span span) : Expr(span)
{
    public override void VisitExpr(Action<Expr> f) { }
}

public sealed class IntLiteral(TokenInt value, Span span) : LiteralExpr(span)
{
    public TokenInt Value { get; } = value;
    internal override void Write(StringBuilder sb) => sb.Append(Value.ToString());
}

public sealed class FloatLiteral(double value, Span span) : LiteralExpr(span)
{
    public double Value { get; } = value;

    internal override void Write(StringBuilder sb)
    {
        if (double.IsInfinity(Value))
        {
            sb.Append(Value < 0 ? "-1e400" : "1e400");
        }
        else
        {
            sb.Append(RustFormat.DebugF64(Value));
        }
    }
}

public sealed class StringLiteral(string value, Span span) : LiteralExpr(span)
{
    public string Value { get; } = value;
    internal override void Write(StringBuilder sb) => WriteStringLiteral(sb, Value);
}

public sealed class BytesLiteral(byte[] value, Span span) : LiteralExpr(span)
{
    public byte[] Value { get; } = value;

    internal override void Write(StringBuilder sb)
    {
        sb.Append("b\"");
        foreach (byte b in Value)
        {
            switch (b)
            {
                case (byte)'"': sb.Append("\\\""); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                case >= 0x20 and <= 0x7e: sb.Append((char)b); break;
                default: sb.Append("\\x").Append(b.ToString("x2")); break;
            }
        }
        sb.Append('"');
    }
}

public sealed class EllipsisLiteral(Span span) : LiteralExpr(span)
{
    internal override void Write(StringBuilder sb) => sb.Append("...");
}

public sealed class NotExpr(Expr operand, Span span) : Expr(span)
{
    public Expr Operand { get; } = operand;
    public override void VisitExpr(Action<Expr> f) => f(Operand);
    internal override void Write(StringBuilder sb) { sb.Append("(not "); Operand.Write(sb); sb.Append(')'); }
}

public sealed class MinusExpr(Expr operand, Span span) : Expr(span)
{
    public Expr Operand { get; } = operand;
    public override void VisitExpr(Action<Expr> f) => f(Operand);
    internal override void Write(StringBuilder sb) { sb.Append('-'); Operand.Write(sb); }
}

public sealed class PlusExpr(Expr operand, Span span) : Expr(span)
{
    public Expr Operand { get; } = operand;
    public override void VisitExpr(Action<Expr> f) => f(Operand);
    internal override void Write(StringBuilder sb) { sb.Append('+'); Operand.Write(sb); }
}

public sealed class BitNotExpr(Expr operand, Span span) : Expr(span)
{
    public Expr Operand { get; } = operand;
    public override void VisitExpr(Action<Expr> f) => f(Operand);
    internal override void Write(StringBuilder sb) { sb.Append('~'); Operand.Write(sb); }
}

public sealed class BinaryExpr(Expr left, BinOp op, Expr right, Span span) : Expr(span)
{
    public Expr Left { get; } = left;
    public BinOp Op { get; } = op;
    public Expr Right { get; } = right;
    public override void VisitExpr(Action<Expr> f) { f(Left); f(Right); }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('('); Left.Write(sb); sb.Append(Op.Padded()); Right.Write(sb); sb.Append(')');
    }
}

/// <summary><c>IfTrue if Condition else IfFalse</c></summary>
public sealed class IfExpr(Expr condition, Expr ifTrue, Expr ifFalse, Span span) : Expr(span)
{
    public Expr Condition { get; } = condition;
    public Expr IfTrue { get; } = ifTrue;
    public Expr IfFalse { get; } = ifFalse;
    public override void VisitExpr(Action<Expr> f) { f(Condition); f(IfTrue); f(IfFalse); }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('('); IfTrue.Write(sb); sb.Append(" if "); Condition.Write(sb); sb.Append(" else "); IfFalse.Write(sb); sb.Append(')');
    }
}

public sealed class ListExpr(IReadOnlyList<Expr> items, Span span) : Expr(span)
{
    public IReadOnlyList<Expr> Items { get; } = items;
    public override void VisitExpr(Action<Expr> f) { foreach (var x in Items) f(x); }
    internal override void Write(StringBuilder sb) { sb.Append('['); WriteList(sb, Items); sb.Append(']'); }
}

public sealed class DictExpr(IReadOnlyList<(Expr Key, Expr Value)> entries, Span span) : Expr(span)
{
    public IReadOnlyList<(Expr Key, Expr Value)> Entries { get; } = entries;

    public override void VisitExpr(Action<Expr> f)
    {
        foreach (var (k, v) in Entries) { f(k); f(v); }
    }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('{');
        for (int i = 0; i < Entries.Count; i++)
        {
            if (i != 0) sb.Append(", ");
            Entries[i].Key.Write(sb); sb.Append(": "); Entries[i].Value.Write(sb);
        }
        sb.Append('}');
    }
}

public sealed class ListComprehension(Expr item, ForClause first, IReadOnlyList<Clause> rest, Span span) : Expr(span)
{
    public Expr Item { get; } = item;
    public ForClause First { get; } = first;
    public IReadOnlyList<Clause> Rest { get; } = rest;

    public override void VisitExpr(Action<Expr> f)
    {
        First.VisitExpr(f);
        foreach (var c in Rest) c.VisitExpr(f);
        f(Item);
    }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('['); Item.Write(sb); First.Write(sb);
        foreach (var c in Rest) c.Write(sb);
        sb.Append(']');
    }
}

public sealed class DictComprehension(Expr key, Expr value, ForClause first, IReadOnlyList<Clause> rest, Span span) : Expr(span)
{
    public Expr Key { get; } = key;
    public Expr Value { get; } = value;
    public ForClause First { get; } = first;
    public IReadOnlyList<Clause> Rest { get; } = rest;

    public override void VisitExpr(Action<Expr> f)
    {
        First.VisitExpr(f);
        foreach (var c in Rest) c.VisitExpr(f);
        f(Key);
        f(Value);
    }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('{'); Key.Write(sb); sb.Append(": "); Value.Write(sb); First.Write(sb);
        foreach (var c in Rest) c.Write(sb);
        sb.Append('}');
    }
}

/// <summary>An f-string, desugared: <see cref="Format"/> has a <c>{}</c> marker per expression.</summary>
public sealed class FStringExpr(string format, IReadOnlyList<Expr> expressions, Span span) : Expr(span)
{
    public string Format { get; } = format;
    public IReadOnlyList<Expr> Expressions { get; } = expressions;
    public override void VisitExpr(Action<Expr> f) { foreach (var x in Expressions) f(x); }

    internal override void Write(StringBuilder sb)
    {
        WriteStringLiteral(sb, Format);
        sb.Append(".format(");
        WriteList(sb, Expressions);
        sb.Append(')');
    }
}

// ======================================================================== supporting nodes

/// <summary>A restricted expression in type position.</summary>
public sealed class TypeExpr(Expr expr) : Node(expr.Span)
{
    public Expr Expr { get; } = expr;
    public override string ToString() => Expr.ToString();
}

public abstract class Argument(Span span) : Node(span)
{
    public abstract Expr Value { get; }

    internal abstract void Write(StringBuilder sb);

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

public sealed class PositionalArgument(Expr value, Span span) : Argument(span)
{
    public override Expr Value { get; } = value;
    internal override void Write(StringBuilder sb) => Value.Write(sb);
}

public sealed class NamedArgument(AstString name, Expr value, Span span) : Argument(span)
{
    public AstString Name { get; } = name;
    public override Expr Value { get; } = value;
    internal override void Write(StringBuilder sb) { sb.Append(Name.Value).Append(" = "); Value.Write(sb); }
}

/// <summary><c>*args</c></summary>
public sealed class StarArgument(Expr value, Span span) : Argument(span)
{
    public override Expr Value { get; } = value;
    internal override void Write(StringBuilder sb) { sb.Append('*'); Value.Write(sb); }
}

/// <summary><c>**kwargs</c></summary>
public sealed class StarStarArgument(Expr value, Span span) : Argument(span)
{
    public override Expr Value { get; } = value;
    internal override void Write(StringBuilder sb) { sb.Append("**"); Value.Write(sb); }
}

/// <summary>An identifier in binding position.</summary>
public sealed class AssignIdent(string name, Span span) : Node(span)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public enum ParameterKind
{
    /// <summary>The <c>/</c> marker.</summary>
    Slash,
    Normal,
    /// <summary>The bare <c>*</c> marker.</summary>
    NoArgs,
    Args,
    KwArgs,
}

public sealed class Parameter(ParameterKind kind, AssignIdent? name, TypeExpr? type, Expr? defaultValue, Span span) : Node(span)
{
    public ParameterKind Kind { get; } = kind;
    public AssignIdent? Name { get; } = name;
    public TypeExpr? Type { get; } = type;
    public Expr? Default { get; } = defaultValue;

    public void VisitExpr(Action<Expr> f)
    {
        if (Type is not null) f(Type.Expr);
        if (Default is not null) f(Default);
    }

    internal void Write(StringBuilder sb)
    {
        switch (Kind)
        {
            case ParameterKind.Slash: sb.Append('/'); return;
            case ParameterKind.NoArgs: sb.Append('*'); return;
            case ParameterKind.Args: sb.Append('*'); break;
            case ParameterKind.KwArgs: sb.Append("**"); break;
        }
        sb.Append(Name!.Name);
        if (Type is not null) sb.Append(": ").Append(Type);
        if (Default is not null) { sb.Append(" = "); Default.Write(sb); }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

public abstract class AssignTarget(Span span) : Node(span)
{
    public abstract void VisitExpr(Action<Expr> f);

    /// <summary>Names bound by this target.</summary>
    public abstract void VisitLvalue(Action<AssignIdent> f);

    internal abstract void Write(StringBuilder sb);

    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }
}

public sealed class TupleTarget(IReadOnlyList<AssignTarget> items, Span span) : AssignTarget(span)
{
    public IReadOnlyList<AssignTarget> Items { get; } = items;
    public override void VisitExpr(Action<Expr> f) { foreach (var t in Items) t.VisitExpr(f); }
    public override void VisitLvalue(Action<AssignIdent> f) { foreach (var t in Items) t.VisitLvalue(f); }

    internal override void Write(StringBuilder sb)
    {
        sb.Append('(');
        for (int i = 0; i < Items.Count; i++)
        {
            if (i != 0) sb.Append(", ");
            Items[i].Write(sb);
        }
        if (Items.Count == 1) sb.Append(',');
        sb.Append(')');
    }
}

public sealed class IndexTarget(Expr target, Expr index, Span span) : AssignTarget(span)
{
    public Expr Target { get; } = target;
    public Expr Index { get; } = index;
    public override void VisitExpr(Action<Expr> f) { f(Target); f(Index); }
    public override void VisitLvalue(Action<AssignIdent> f) { }
    internal override void Write(StringBuilder sb) { Target.WritePostfixReceiver(sb); sb.Append('['); Index.Write(sb); sb.Append(']'); }
}

public sealed class DotTarget(Expr target, AstString field, Span span) : AssignTarget(span)
{
    public Expr Target { get; } = target;
    public AstString Field { get; } = field;
    public override void VisitExpr(Action<Expr> f) => f(Target);
    public override void VisitLvalue(Action<AssignIdent> f) { }

    internal override void Write(StringBuilder sb)
    {
        if (Target is IntLiteral)
        {
            sb.Append('('); Target.Write(sb); sb.Append(").").Append(Field.Value);
        }
        else
        {
            Target.WritePostfixReceiver(sb); sb.Append('.').Append(Field.Value);
        }
    }
}

public sealed class IdentTarget(AssignIdent ident, Span span) : AssignTarget(span)
{
    public AssignIdent Ident { get; } = ident;
    public override void VisitExpr(Action<Expr> f) { }
    public override void VisitLvalue(Action<AssignIdent> f) => f(Ident);
    internal override void Write(StringBuilder sb) => sb.Append(Ident.Name);
}

public sealed class ForClause(AssignTarget variable, Expr over)
{
    public AssignTarget Variable { get; } = variable;
    public Expr Over { get; } = over;

    public void VisitExpr(Action<Expr> f) { Variable.VisitExpr(f); f(Over); }

    internal void Write(StringBuilder sb)
    {
        sb.Append(" for "); Variable.Write(sb); sb.Append(" in "); Over.Write(sb);
    }
}

/// <summary>A comprehension clause after the first: either another <c>for</c> or an <c>if</c>.</summary>
public abstract class Clause
{
    public abstract void VisitExpr(Action<Expr> f);
    internal abstract void Write(StringBuilder sb);
}

public sealed class ForClauseClause(ForClause clause) : Clause
{
    public ForClause For { get; } = clause;
    public override void VisitExpr(Action<Expr> f) => For.VisitExpr(f);
    internal override void Write(StringBuilder sb) => For.Write(sb);
}

public sealed class IfClause(Expr condition) : Clause
{
    public Expr Condition { get; } = condition;
    public override void VisitExpr(Action<Expr> f) => f(Condition);
    internal override void Write(StringBuilder sb) { sb.Append(" if "); Condition.Write(sb); }
}

// ======================================================================== statements

public abstract class Stmt(Span span) : Node(span)
{
    /// <summary>Call <paramref name="f"/> on each directly nested statement.</summary>
    public abstract void VisitStmt(Action<Stmt> f);

    /// <summary>Call <paramref name="f"/> on each expression directly in this statement or nested statements,
    /// without descending into expression children.</summary>
    public void VisitExpr(Action<Expr> f)
    {
        VisitOwnExprs(f);
        VisitStmt(s => s.VisitExpr(f));
    }

    /// <summary>Expressions owned directly by this statement (not by nested statements).</summary>
    public abstract void VisitOwnExprs(Action<Expr> f);

    internal abstract void Write(StringBuilder sb, string tab);

    public sealed override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb, "");
        return sb.ToString();
    }
}

public sealed class BreakStmt(Span span) : Stmt(span)
{
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) { }
    internal override void Write(StringBuilder sb, string tab) => sb.Append(tab).Append("break\n");
}

public sealed class ContinueStmt(Span span) : Stmt(span)
{
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) { }
    internal override void Write(StringBuilder sb, string tab) => sb.Append(tab).Append("continue\n");
}

public sealed class PassStmt(Span span) : Stmt(span)
{
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) { }
    internal override void Write(StringBuilder sb, string tab) => sb.Append(tab).Append("pass\n");
}

public sealed class ReturnStmt(Expr? value, Span span) : Stmt(span)
{
    public Expr? Value { get; } = value;
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) { if (Value is not null) f(Value); }

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab).Append("return");
        if (Value is not null) { sb.Append(' '); Value.Write(sb); }
        sb.Append('\n');
    }
}

public sealed class ExpressionStmt(Expr expr, Span span) : Stmt(span)
{
    public Expr Expr { get; } = expr;
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) => f(Expr);
    internal override void Write(StringBuilder sb, string tab) { sb.Append(tab); Expr.Write(sb); sb.Append('\n'); }
}

/// <summary><c>x: t = y</c></summary>
public sealed class AssignStmt(AssignTarget target, TypeExpr? type, Expr value, Span span) : Stmt(span)
{
    public AssignTarget Target { get; } = target;
    public TypeExpr? Type { get; } = type;
    public Expr Value { get; } = value;
    public override void VisitStmt(Action<Stmt> f) { }

    public override void VisitOwnExprs(Action<Expr> f)
    {
        Target.VisitExpr(f);
        if (Type is not null) f(Type.Expr);
        f(Value);
    }

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab); Target.Write(sb); sb.Append(' ');
        if (Type is not null) sb.Append(": ").Append(Type).Append(' ');
        sb.Append("= "); Value.Write(sb); sb.Append('\n');
    }
}

/// <summary><c>x += y</c> and friends. The target is never a tuple.</summary>
public sealed class AssignModifyStmt(AssignTarget target, AssignOp op, Expr value, Span span) : Stmt(span)
{
    public AssignTarget Target { get; } = target;
    public AssignOp Op { get; } = op;
    public Expr Value { get; } = value;
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) { Target.VisitExpr(f); f(Value); }

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab); Target.Write(sb); sb.Append(Op.Padded()); Value.Write(sb); sb.Append('\n');
    }
}

public sealed class StatementsStmt(IReadOnlyList<Stmt> statements, Span span) : Stmt(span)
{
    public IReadOnlyList<Stmt> Statements { get; } = statements;
    public override void VisitStmt(Action<Stmt> f) { foreach (var s in Statements) f(s); }
    public override void VisitOwnExprs(Action<Expr> f) { }
    internal override void Write(StringBuilder sb, string tab) { foreach (var s in Statements) s.Write(sb, tab); }
}

public sealed class IfStmt(Expr condition, Stmt then, Stmt? otherwise, Span span) : Stmt(span)
{
    public Expr Condition { get; } = condition;
    public Stmt Then { get; } = then;

    /// <summary>The else branch (an <c>elif</c> is a nested <see cref="IfStmt"/>), if any.</summary>
    public Stmt? Else { get; } = otherwise;

    public override void VisitStmt(Action<Stmt> f) { f(Then); if (Else is not null) f(Else); }
    public override void VisitOwnExprs(Action<Expr> f) => f(Condition);

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab).Append("if "); Condition.Write(sb); sb.Append(":\n");
        Then.Write(sb, tab + "  ");
        if (Else is not null)
        {
            sb.Append(tab).Append("else:\n");
            Else.Write(sb, tab + "  ");
        }
    }
}

public sealed class ForStmt(AssignTarget variable, Expr over, Stmt body, Span span) : Stmt(span)
{
    public AssignTarget Variable { get; } = variable;
    public Expr Over { get; } = over;
    public Stmt Body { get; } = body;
    public override void VisitStmt(Action<Stmt> f) => f(Body);
    public override void VisitOwnExprs(Action<Expr> f) { Variable.VisitExpr(f); f(Over); }

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab).Append("for "); Variable.Write(sb); sb.Append(" in "); Over.Write(sb); sb.Append(":\n");
        Body.Write(sb, tab + "  ");
    }
}

public sealed class DefStmt(AssignIdent name, IReadOnlyList<Parameter> parameters, TypeExpr? returnType, Stmt body, Span span) : Stmt(span)
{
    public AssignIdent Name { get; } = name;
    public IReadOnlyList<Parameter> Parameters { get; } = parameters;
    public TypeExpr? ReturnType { get; } = returnType;
    public Stmt Body { get; } = body;
    public override void VisitStmt(Action<Stmt> f) => f(Body);

    public override void VisitOwnExprs(Action<Expr> f)
    {
        foreach (var p in Parameters) p.VisitExpr(f);
        if (ReturnType is not null) f(ReturnType.Expr);
    }

    /// <summary>Span of the name, parameters and return type.</summary>
    public Span SignatureSpan
    {
        get
        {
            var span = Name.Span;
            foreach (var p in Parameters) span = span.Merge(p.Span);
            return ReturnType is null ? span : span.Merge(ReturnType.Span);
        }
    }

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab).Append("def ").Append(Name.Name).Append('(');
        for (int i = 0; i < Parameters.Count; i++)
        {
            if (i != 0) sb.Append(", ");
            Parameters[i].Write(sb);
        }
        sb.Append(')');
        if (ReturnType is not null) sb.Append(" -> ").Append(ReturnType);
        sb.Append(":\n");
        Body.Write(sb, tab + "  ");
    }
}

/// <summary>One symbol in a <c>load</c>: <c>local = "their"</c>, or just <c>"their"</c>.</summary>
public sealed class LoadArg(AssignIdent local, AstString their, Span? comma)
{
    public AssignIdent Local { get; } = local;
    public AstString Their { get; } = their;

    /// <summary>Span of the trailing comma, if there was one.</summary>
    public Span? Comma { get; } = comma;

    public Span Span => Local.Span.Merge(Their.Span);
    public Span SpanWithTrailingComma => Comma is { } c ? Span.Merge(c) : Span;
}

public sealed class LoadStmt(AstString module, IReadOnlyList<LoadArg> args, Span span) : Stmt(span)
{
    public AstString Module { get; } = module;
    public IReadOnlyList<LoadArg> Args { get; } = args;
    public override void VisitStmt(Action<Stmt> f) { }
    public override void VisitOwnExprs(Action<Expr> f) { }

    internal override void Write(StringBuilder sb, string tab)
    {
        sb.Append(tab).Append("load(");
        Expr.WriteStringLiteral(sb, Module.Value);
        sb.Append(", ");
        for (int i = 0; i < Args.Count; i++)
        {
            if (i != 0) sb.Append(", ");
            var a = Args[i];
            if (a.Local.Name == a.Their.Value)
            {
                Expr.WriteStringLiteral(sb, a.Their.Value);
            }
            else
            {
                sb.Append(a.Local.Name).Append(" = ");
                Expr.WriteStringLiteral(sb, a.Their.Value);
            }
        }
        sb.Append(")\n");
    }
}
