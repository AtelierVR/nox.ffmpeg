using System;
using System.Collections.Generic;

namespace FFmpeg.AutoGen.ClangMacroParser.Expressions
{
    public static class OperationTypeExtensions
    {
        // http://en.cppreference.com/w/c/language/operator_precedence
        private static readonly Dictionary<OperationType, int> OperationPrecedence = new()
        {
            { OperationType.Add, 4 },
            { OperationType.Divide, 3 },
            { OperationType.Modulo, 3 },
            { OperationType.Multiply, 3 },
            { OperationType.Power, 9 },
            { OperationType.Subtract, 4 },
            { OperationType.And, 11 },
            { OperationType.Or, 10 },
            { OperationType.ExclusiveOr, 9 },
            { OperationType.LeftShift, 5 },
            { OperationType.RightShift, 5 },
            { OperationType.AndAlso, 11 },
            { OperationType.OrElse, 12 },
            { OperationType.Equal, 7 },
            { OperationType.NotEqual, 7 },
            { OperationType.GreaterThanOrEqual, 6 },
            { OperationType.GreaterThan, 6 },
            { OperationType.LessThan, 6 },
            { OperationType.LessThanOrEqual, 6 }
        };

        public static int GetPrecedence(this OperationType operationType) => OperationPrecedence[operationType];

        public static OperationType ToOperationType(this string value)
            => value switch
            {
                "+" => OperationType.Add,
                "/" => OperationType.Divide,
                "%" => OperationType.Modulo,
                "*" => OperationType.Multiply,
                "^" => OperationType.Power,
                "-" => OperationType.Subtract,
                "&" => OperationType.And,
                "|" => OperationType.Or,
                "~" => OperationType.ExclusiveOr,
                "<<" => OperationType.LeftShift,
                ">>" => OperationType.RightShift,
                "&&" => OperationType.AndAlso,
                "||" => OperationType.OrElse,
                "==" => OperationType.Equal,
                "!=" => OperationType.NotEqual,
                ">=" => OperationType.GreaterThanOrEqual,
                ">" => OperationType.GreaterThan,
                "<" => OperationType.LessThan,
                "<=" => OperationType.LessThanOrEqual,
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            };

        public static string ToOperationTypeString(this OperationType operationType)
            => operationType switch
            {
                OperationType.Add => "+",
                OperationType.Divide => "/",
                OperationType.Modulo => "%",
                OperationType.Multiply => "*",
                OperationType.Power => "^",
                OperationType.Subtract => "-",
                OperationType.And => "&",
                OperationType.Or => "|",
                OperationType.ExclusiveOr => "~",
                OperationType.LeftShift => "<<",
                OperationType.RightShift => ">>",
                OperationType.AndAlso => "&&",
                OperationType.OrElse => "||",
                OperationType.Equal => "==",
                OperationType.NotEqual => "!=",
                OperationType.GreaterThanOrEqual => ">=",
                OperationType.GreaterThan => ">",
                OperationType.LessThan => "<",
                OperationType.LessThanOrEqual => "<=",
                _ => throw new ArgumentOutOfRangeException(nameof(operationType)),
            };

        public static bool IsArithmetic(this OperationType operationType)
            => operationType == OperationType.Add ||
               operationType == OperationType.Divide ||
               operationType == OperationType.Modulo ||
               operationType == OperationType.Multiply ||
               operationType == OperationType.Power ||
               operationType == OperationType.Subtract;

        public static bool IsBitwise(this OperationType operationType)
            => operationType == OperationType.And ||
               operationType == OperationType.Or ||
               operationType == OperationType.ExclusiveOr;

        public static bool IsShift(this OperationType operationType)
            => operationType == OperationType.LeftShift ||
               operationType == OperationType.RightShift;

        public static bool IsConditional(this OperationType operationType)
            => operationType == OperationType.AndAlso ||
               operationType == OperationType.OrElse;

        public static bool IsComparison(this OperationType operationType)
            => operationType == OperationType.Equal ||
               operationType == OperationType.NotEqual ||
               operationType == OperationType.GreaterThan ||
               operationType == OperationType.GreaterThanOrEqual ||
               operationType == OperationType.LessThan ||
               operationType == OperationType.LessThanOrEqual;
    }
}
