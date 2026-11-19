using System.Globalization;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Obsidian.Dumper.Core.Inspection;

/// <summary>Readable IL listing generator (dnlib based, no external disassembler).</summary>
public static class IlFormatter
{
    private const int OpCodeColumnWidth = 12;

    public static string Format(ModuleDefMD module, MethodDef method)
    {
        int instructionHint = 0;
        try
        {
            if (method.HasBody)
                instructionHint = method.Body.Instructions.Count;
        }
        catch
        {
            instructionHint = 0;
        }

        var builder = new StringBuilder(Math.Clamp(4096 + instructionHint * 48, 4096, 4 * 1024 * 1024));
        try
        {
            string declaring = method.DeclaringType?.FullName ?? "?";
            builder.Append("// ").Append(declaring).Append("::").Append(method.Name).AppendLine();
            builder.Append("// token 0x").Append(((uint)method.MDToken.Raw).ToString("X8"))
                   .Append(" · rva 0x").Append(((uint)method.RVA).ToString("X"))
                   .Append(" · attributes ").Append(method.Attributes).AppendLine();

            if (!method.HasBody)
            {
                builder.AppendLine("// no method body");
                return builder.ToString();
            }

            CilBody body = method.Body;
            int codeSize = body.Instructions.Sum(i => i.GetSize());
            builder.Append("// IL ").Append(codeSize).Append(" bytes · maxstack ")
                   .Append(body.MaxStack).Append(" · locals ").Append(body.Variables.Count).AppendLine();

            if (body.Variables.Count > 0)
            {
                builder.Append(".locals init (");
                for (int i = 0; i < body.Variables.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append('[').Append(i).Append("] ").Append(body.Variables[i].Type?.TypeName ?? "?");
                }
                builder.AppendLine(")");
                builder.AppendLine();
            }

            HashSet<uint> targets = CollectTargets(body);
            foreach (Instruction instruction in body.Instructions)
            {
                if (targets.Contains(instruction.Offset))
                    builder.Append("IL_").Append(instruction.Offset.ToString("X4")).AppendLine(":");

                string opCode = instruction.OpCode.Name;
                builder.Append("  IL_").Append(instruction.Offset.ToString("X4")).Append(": ")
                       .Append(opCode)
                       .Append(' ', Math.Max(1, OpCodeColumnWidth - opCode.Length))
                       .Append(FormatOperand(instruction, body))
                       .AppendLine();
            }

            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                builder.AppendLine();
                builder.Append("// EH ").Append(handler.HandlerType).Append(": try IL_")
                       .Append(Label(handler.TryStart))
                       .Append("-IL_").Append(Label(handler.TryEnd))
                       .Append(" handler IL_").Append(Label(handler.HandlerStart))
                       .Append("-IL_").Append(Label(handler.HandlerEnd));
                if (handler.HandlerType == ExceptionHandlerType.Filter)
                    builder.Append(" filter IL_").Append(Label(handler.FilterStart));
                if (handler.CatchType != null)
                    builder.Append(" catch ").Append(handler.CatchType.FullName);
                builder.AppendLine();
            }

            return builder.ToString();
        }
        catch (Exception ex)
        {
            // Keep whatever was produced so far: a partially decoded method is
            // still useful evidence, unlike a bare error string.
            builder.AppendLine();
            builder.AppendLine("// IL unavailable: " + ex.GetType().Name + ": " + ex.Message);
            return builder.ToString();
        }
    }

    private static string Label(Instruction? instruction)
        => instruction == null ? "?" : instruction.Offset.ToString("X4");

    private static HashSet<uint> CollectTargets(CilBody body)
    {
        var targets = new HashSet<uint>();
        foreach (Instruction instruction in body.Instructions)
        {
            if (instruction.Operand is Instruction target)
                targets.Add(target.Offset);
            else if (instruction.Operand is Instruction[] many)
                foreach (Instruction item in many)
                    targets.Add(item.Offset);
        }

        foreach (ExceptionHandler handler in body.ExceptionHandlers)
        {
            if (handler.TryStart != null) targets.Add(handler.TryStart.Offset);
            if (handler.TryEnd != null) targets.Add(handler.TryEnd.Offset);
            if (handler.HandlerStart != null) targets.Add(handler.HandlerStart.Offset);
            if (handler.HandlerEnd != null) targets.Add(handler.HandlerEnd.Offset);
            if (handler.FilterStart != null) targets.Add(handler.FilterStart.Offset);
        }

        return targets;
    }

    private static string FormatOperand(Instruction instruction, CilBody body)
    {
        object? operand = instruction.Operand;
        switch (operand)
        {
            case null:
                return string.Empty;
            case string text:
                return "\"" + Escape(text) + "\"";
            case Instruction target:
                return "IL_" + target.Offset.ToString("X4");
            case Instruction[] group:
                return "(" + string.Join(", ", group.Select(t => "IL_" + t.Offset.ToString("X4"))) + ")";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(operand, CultureInfo.InvariantCulture) ?? string.Empty;
            case float value:
                return value.ToString("0.0############", CultureInfo.InvariantCulture);
            case double value:
                return value.ToString("0.0############", CultureInfo.InvariantCulture);
            // A MemberRef can describe a method, a field or a type token. The
            // reference kind has to win over the interface pattern order.
            case MemberRef memberRef when memberRef.IsFieldRef:
                return FormatField(memberRef);
            case MemberRef memberRef when !memberRef.IsMethodRef && memberRef.Class != null:
                return memberRef.Class.FullName;
            case IMethod method:
                return FormatMethod(method);
            case IField field:
                return FormatField(field);
            case ITypeDefOrRef type:
                return type.FullName;
            case MethodSig signature:
                return FormatSignature(signature);
            case Local local:
                return FormatLocal(local, body);
            case Parameter parameter:
                return "arg_" + parameter.Index +
                       (string.IsNullOrEmpty(parameter.Name) ? string.Empty : " (" + parameter.Name + ")");
            default:
                return operand.ToString() ?? string.Empty;
        }
    }

    private static string FormatLocal(Local local, CilBody body)
    {
        string name = string.IsNullOrEmpty(local.Name) ? string.Empty : " (" + local.Name + ")";
        for (int i = 0; i < body.Variables.Count; i++)
        {
            if (ReferenceEquals(body.Variables[i], local))
                return "loc_" + i + name;
        }
        return "loc_?" + name;
    }

    private static string FormatSignature(MethodSig signature)
    {
        var parameters = new List<string>(signature.Params.Count);
        foreach (TypeSig parameter in signature.Params)
            parameters.Add(parameter.TypeName);
        return $"{signature.RetType?.TypeName ?? "void"} ({string.Join(", ", parameters)})";
    }

    private static string FormatMethod(IMethod method)
    {
        string declaring = method.DeclaringType?.FullName ?? string.Empty;
        var parameters = new List<string>(method.MethodSig?.Params.Count ?? 0);
        if (method.MethodSig != null)
        {
            foreach (TypeSig parameter in method.MethodSig.Params)
                parameters.Add(parameter.TypeName);
        }

        string name = method.Name ?? "?";
        bool isCtor = name is ".ctor" or ".cctor";
        string returnType = isCtor ? string.Empty : (method.MethodSig?.RetType?.TypeName ?? "void") + " ";
        return $"{returnType}{(declaring.Length > 0 ? declaring + "::" : string.Empty)}{name}({string.Join(", ", parameters)})";
    }

    private static string FormatField(IField field)
        => $"{field.DeclaringType?.FullName}::{field.Name}";

    private static string Escape(string value)
    {
        bool truncated = value.Length > 512;
        string text = truncated ? value[..512] : value;
        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (char.IsControl(c))
                        builder.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        builder.Append(c);
                    break;
            }
        }
        if (truncated)
            builder.Append('…');
        return builder.ToString();
    }
}
