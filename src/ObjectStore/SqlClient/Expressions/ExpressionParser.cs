﻿using ObjectStore.Expressions;
using ObjectStore.OrMapping;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ObjectStore.SqlClient
{
    public partial class DataBaseProvider 
    {
        partial void InitExpressionParser()
        {
            _expressionParser = new ExpressionParser()
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} + {args.ParseChild(exp.Right)}", ExpressionType.Add, ExpressionType.AddChecked)
                        .AddRule<BinaryExpression>((exp, args) => $"({args.ParseChild(exp.Left)}) AND ({args.ParseChild(exp.Right)})", ExpressionType.And, ExpressionType.AndAlso)
                        .AddRule<ConstantExpression>((exp, args) => args.GetService<IParsingContext>().GetParameter(((IFillAbleObject)exp.Value).Keys.Single()),
                            e => e.Value is IFillAbleObject, ExpressionType.Constant)
                        .AddRule<ConstantExpression>((exp, args) =>
                        {
                            string[] values = ((IEnumerable)exp.Value).OfType<object>().Select(x => args.ParseChild(Expression.Constant(x))).ToArray();
                            return values.Length == 0 ? "(SELECT NULL)" : $"({string.Join(", ", values)})";
                        }, e => !(e.Value is string) && e.Value is IEnumerable, ExpressionType.Constant)
                        .AddRule<ConstantExpression>((exp, args) => args.GetService<IParsingContext>().GetParameter(exp.Value))
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} IS NULL", x => x.Right is ConstantExpression && ((ConstantExpression)x.Right).Value == null, ExpressionType.Equal)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Right)} IS NULL", x => x.Left is ConstantExpression && ((ConstantExpression)x.Left).Value == null, ExpressionType.Equal)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} IS NOT NULL", x => x.Right is ConstantExpression && ((ConstantExpression)x.Right).Value == null, ExpressionType.NotEqual)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Right)} IS NOT NULL", x => x.Left is ConstantExpression && ((ConstantExpression)x.Left).Value == null, ExpressionType.NotEqual)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} = {args.ParseChild(exp.Right)}", ExpressionType.Equal)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} != {args.ParseChild(exp.Right)}", ExpressionType.NotEqual)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} > {args.ParseChild(exp.Right)}", ExpressionType.GreaterThan)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} >= {args.ParseChild(exp.Right)}", ExpressionType.GreaterThanOrEqual)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} < {args.ParseChild(exp.Right)}", ExpressionType.LessThan)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} <= {args.ParseChild(exp.Right)}", ExpressionType.LessThanOrEqual)
                        .AddRule<BinaryExpression>((exp, args) => $"{args.ParseChild(exp.Left)} - {args.ParseChild(exp.Right)}", ExpressionType.Subtract, ExpressionType.SubtractChecked)
                        .AddRule<MemberExpression>((exp, args) => $"{args.ParseChild(exp.Expression)}.{Mapping.GetMapping((PropertyInfo)exp.Member).FieldName}",
                            e => IsPropertyInfo(e.Member) && e.Expression.NodeType == ExpressionType.Parameter,
                            ExpressionType.MemberAccess)
                        .AddRule<MemberExpression>((exp, args) => $"{args.GetService<IParsingContext>().GetJoin((MemberExpression)exp.Expression)}.{Mapping.GetMapping((PropertyInfo)exp.Member).FieldName}",
                            e => IsPropertyInfo(e.Member) && 
                            e.Expression is MemberExpression &&
                            IsPropertyInfo(((MemberExpression)e.Expression).Member) &&
                            ((MemberExpression)e.Expression).Member.GetCustomAttributes(typeof(ForeignObjectMappingAttribute), true).Any(), 
                            ExpressionType.MemberAccess)
                        .AddRule<MethodCallExpression>((exp, args) => $"{args.ParseChild(exp.Arguments[1])} IN ({string.Join(", ", ((IEnumerable)Expression.Lambda(exp.Arguments[0]).Compile().DynamicInvoke()).OfType<object>().Select(x => args.GetService<IParsingContext>().GetParameter(x)).ToArray())})", x => x.Method.DeclaringType == typeof(Enumerable) && x.Method.Name == nameof(Enumerable.Contains) && x.Arguments.Count == 2)
                        .AddRule<ParameterExpression>((exp, args) => args.GetService<IParsingContext>().GetAlias(exp))
                        .AddRule<LambdaExpression>((exp, args) => args.ParseChild(exp.Body))
                        .AddRule<UnaryExpression>((exp, args) => args.ParseChild(exp.Operand), ExpressionType.Convert, ExpressionType.ConvertChecked);
        }

        static bool IsPropertyInfo(MemberInfo memberInfo)
        {
#if DNXCORE50 || DOTNET5_4
            return memberInfo is PropertyInfo;
#else
            return memberInfo.MemberType == MemberTypes.Property;
#endif
        }
    }
}
