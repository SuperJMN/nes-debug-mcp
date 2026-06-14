using System.Globalization;

namespace Nes.Debug.Core;

public interface INesBreakpointConditionContext
{
    NesCpuRegisters Registers { get; }

    DebugResult<byte> ReadByte(ushort address);
}

public sealed class BreakpointCondition
{
    private static readonly string[] Operators = ["==", "!=", "<=", ">=", "<", ">"];

    private readonly LeftOperand left;
    private readonly ComparisonOperator comparisonOperator;
    private readonly int right;

    private BreakpointCondition(LeftOperand left, ComparisonOperator comparisonOperator, int right)
    {
        this.left = left;
        this.comparisonOperator = comparisonOperator;
        this.right = right;
    }

    public static bool TryParse(string? expression, out BreakpointCondition? condition, out string? errorMessage)
    {
        condition = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var text = expression.Trim();
        if (!TrySplitComparison(text, out var leftText, out var operatorToken, out var rightText))
        {
            errorMessage = "Expected '<left> <operator> <constant>' with operator ==, !=, <, <=, >, or >=.";
            return false;
        }

        if (!TryParseLeftOperand(leftText, out var leftOperand, out errorMessage))
        {
            return false;
        }

        if (!TryParseConstant(rightText, out var rightValue))
        {
            errorMessage = "Right operand must be a decimal or 0x-prefixed hexadecimal constant between 0 and 0xFFFF.";
            return false;
        }

        condition = new BreakpointCondition(leftOperand, ParseOperator(operatorToken), rightValue);
        return true;
    }

    public DebugResult<bool> Evaluate(INesBreakpointConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var leftValue = left.Read(context);
        if (!leftValue.IsSuccess)
        {
            return DebugResult<bool>.Failure(leftValue.Error!.Code, leftValue.Error.Message);
        }

        var result = comparisonOperator switch
        {
            ComparisonOperator.Equal => leftValue.Value == right,
            ComparisonOperator.NotEqual => leftValue.Value != right,
            ComparisonOperator.LessThan => leftValue.Value < right,
            ComparisonOperator.LessThanOrEqual => leftValue.Value <= right,
            ComparisonOperator.GreaterThan => leftValue.Value > right,
            ComparisonOperator.GreaterThanOrEqual => leftValue.Value >= right,
            _ => throw new InvalidOperationException($"Unsupported operator: {comparisonOperator}"),
        };

        return DebugResult<bool>.Success(result);
    }

    private static bool TrySplitComparison(
        string text,
        out string leftText,
        out string operatorToken,
        out string rightText)
    {
        foreach (var candidate in Operators)
        {
            var index = text.IndexOf(candidate, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            leftText = text[..index].Trim();
            operatorToken = candidate;
            rightText = text[(index + candidate.Length)..].Trim();
            return leftText.Length > 0 && rightText.Length > 0;
        }

        leftText = "";
        operatorToken = "";
        rightText = "";
        return false;
    }

    private static bool TryParseLeftOperand(string text, out LeftOperand operand, out string? errorMessage)
    {
        errorMessage = null;
        operand = default;

        if (text[0] == '[' || text[^1] == ']')
        {
            if (text.Length < 3 || text[0] != '[' || text[^1] != ']')
            {
                errorMessage = "Memory operands must use [addr] or [PC].";
                return false;
            }

            var inner = text[1..^1].Trim();
            if (TryParseConstant(inner, out var address))
            {
                operand = LeftOperand.MemoryAddress((ushort)address);
                return true;
            }

            if (IsWordRegister(inner))
            {
                operand = LeftOperand.MemoryRegister(inner);
                return true;
            }

            errorMessage = "Memory operands must dereference a decimal or 0x-prefixed address, or PC.";
            return false;
        }

        if (IsByteRegister(text) || IsWordRegister(text))
        {
            operand = LeftOperand.Register(text);
            return true;
        }

        errorMessage = "Left operand must be a register or memory operand.";
        return false;
    }

    private static bool TryParseConstant(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        var style = NumberStyles.None;
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
            style = NumberStyles.HexNumber;
            if (normalized.Length == 0)
            {
                return false;
            }
        }
        else if (normalized.StartsWith('$'))
        {
            normalized = normalized[1..];
            style = NumberStyles.HexNumber;
            if (normalized.Length == 0)
            {
                return false;
            }
        }
        else if (normalized.Any(character => !char.IsDigit(character)))
        {
            return false;
        }

        if (!uint.TryParse(normalized, style, CultureInfo.InvariantCulture, out var parsed) || parsed > ushort.MaxValue)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }

    private static ComparisonOperator ParseOperator(string operatorToken) => operatorToken switch
    {
        "==" => ComparisonOperator.Equal,
        "!=" => ComparisonOperator.NotEqual,
        "<" => ComparisonOperator.LessThan,
        "<=" => ComparisonOperator.LessThanOrEqual,
        ">" => ComparisonOperator.GreaterThan,
        ">=" => ComparisonOperator.GreaterThanOrEqual,
        _ => throw new InvalidOperationException($"Unsupported operator: {operatorToken}"),
    };

    private static bool IsByteRegister(string name) => name.ToUpperInvariant() is "A" or "X" or "Y" or "SP" or "STATUS";

    private static bool IsWordRegister(string name) => name.ToUpperInvariant() is "PC";

    private enum ComparisonOperator
    {
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
    }

    private readonly record struct LeftOperand(LeftOperandKind Kind, string? RegisterName, ushort Address)
    {
        public static LeftOperand Register(string name) => new(LeftOperandKind.Register, name.ToUpperInvariant(), 0);

        public static LeftOperand MemoryAddress(ushort address) => new(LeftOperandKind.MemoryAddress, null, address);

        public static LeftOperand MemoryRegister(string name) => new(LeftOperandKind.MemoryRegister, name.ToUpperInvariant(), 0);

        public DebugResult<int> Read(INesBreakpointConditionContext context)
        {
            return Kind switch
            {
                LeftOperandKind.Register => ReadRegister(context.Registers, RegisterName!),
                LeftOperandKind.MemoryAddress => ReadMemory(context, Address),
                LeftOperandKind.MemoryRegister => ReadMemoryAtRegister(context),
                _ => DebugResult<int>.Failure("invalid_breakpoint_condition", "Unsupported left operand."),
            };
        }

        private DebugResult<int> ReadMemoryAtRegister(INesBreakpointConditionContext context)
        {
            var address = ReadRegister(context.Registers, RegisterName!);
            return address.IsSuccess
                ? ReadMemory(context, (ushort)address.Value)
                : address;
        }

        private static DebugResult<int> ReadMemory(INesBreakpointConditionContext context, ushort address)
        {
            var value = context.ReadByte(address);
            return value.IsSuccess
                ? DebugResult<int>.Success(value.Value)
                : DebugResult<int>.Failure(value.Error!.Code, value.Error.Message);
        }

        private static DebugResult<int> ReadRegister(NesCpuRegisters registers, string name)
        {
            var text = name switch
            {
                "A" => registers.A,
                "X" => registers.X,
                "Y" => registers.Y,
                "SP" => registers.Sp,
                "STATUS" => registers.Status,
                "PC" => registers.Pc,
                _ => throw new InvalidOperationException($"Unsupported register: {name}"),
            };
            var maxValue = IsByteRegister(name) ? byte.MaxValue : ushort.MaxValue;
            return TryParseRegisterValue(text, maxValue, out var value)
                ? DebugResult<int>.Success(value)
                : DebugResult<int>.Failure("invalid_register_value", $"Register {name} has invalid value '{text}'.");
        }

        private static bool TryParseRegisterValue(string text, int maxValue, out int value)
        {
            value = 0;
            var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            if (!uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) || parsed > maxValue)
            {
                return false;
            }

            value = (int)parsed;
            return true;
        }
    }

    private enum LeftOperandKind
    {
        Register,
        MemoryAddress,
        MemoryRegister,
    }
}
