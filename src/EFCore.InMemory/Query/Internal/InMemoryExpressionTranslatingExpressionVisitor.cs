﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.InMemory.Query.Internal
{
    public class InMemoryExpressionTranslatingExpressionVisitor : ExpressionVisitor
    {
        private const string CompiledQueryParameterPrefix = "__";

        private readonly QueryableMethodTranslatingExpressionVisitor _queryableMethodTranslatingExpressionVisitor;
        private readonly EntityProjectionFindingExpressionVisitor _entityProjectionFindingExpressionVisitor;

        public InMemoryExpressionTranslatingExpressionVisitor(
            QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
        {
            _queryableMethodTranslatingExpressionVisitor = queryableMethodTranslatingExpressionVisitor;
            _entityProjectionFindingExpressionVisitor = new EntityProjectionFindingExpressionVisitor();
        }

        private class EntityProjectionFindingExpressionVisitor : ExpressionVisitor
        {
            private bool _found;
            public bool Find(Expression expression)
            {
                _found = false;

                Visit(expression);

                return _found;
            }

            public override Expression Visit(Expression expression)
            {
                if (_found)
                {
                    return expression;
                }

                if (expression is EntityProjectionExpression)
                {
                    _found = true;
                    return expression;
                }

                return base.Visit(expression);
            }
        }

        public virtual Expression Translate(Expression expression)
        {
            var result = Visit(expression);

            return _entityProjectionFindingExpressionVisitor.Find(result)
                ? null
                : result;
        }

        protected override Expression VisitBinary(BinaryExpression binaryExpression)
        {
            var left = Visit(binaryExpression.Left);
            var right = Visit(binaryExpression.Right);
            if (left == null || right == null)
            {
                return null;
            }

            return binaryExpression.Update(left, binaryExpression.Conversion, right);
        }

        protected override Expression VisitConditional(ConditionalExpression conditionalExpression)
        {
            var test = Visit(conditionalExpression.Test);
            var ifTrue = Visit(conditionalExpression.IfTrue);
            var ifFalse = Visit(conditionalExpression.IfFalse);
            if (test == null || ifTrue == null || ifFalse == null)
            {
                return null;
            }

            return conditionalExpression.Update(test, ifTrue, ifFalse);
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            var innerExpression = Visit(memberExpression.Expression);
            if (memberExpression.Expression != null && innerExpression == null)
            {
                return null;
            }

            if ((innerExpression is EntityProjectionExpression
                || (innerExpression is UnaryExpression innerUnaryExpression
                    && innerUnaryExpression.NodeType == ExpressionType.Convert
                    && innerUnaryExpression.Operand is EntityProjectionExpression))
                && TryBindMember(innerExpression, MemberIdentity.Create(memberExpression.Member), memberExpression.Type, out var result))
            {
                return result;
            }

            return memberExpression.Update(innerExpression);
        }

        private bool TryBindMember(Expression source, MemberIdentity memberIdentity, Type type, out Expression result)
        {
            result = null;
            Type convertedType = null;
            if (source is UnaryExpression unaryExpression
                && unaryExpression.NodeType == ExpressionType.Convert)
            {
                source = unaryExpression.Operand;
                if (unaryExpression.Type != typeof(object))
                {
                    convertedType = unaryExpression.Type;
                }
            }

            if (source is EntityProjectionExpression entityProjection)
            {
                var entityType = entityProjection.EntityType;
                if (convertedType != null
                    && !(convertedType.IsInterface
                         && convertedType.IsAssignableFrom(entityType.ClrType)))
                {
                    entityType = entityType.GetRootType().GetDerivedTypesInclusive()
                        .FirstOrDefault(et => et.ClrType == convertedType);
                    if (entityType == null)
                    {
                        return false;
                    }
                }

                var property = memberIdentity.MemberInfo != null
                    ? entityType.FindProperty(memberIdentity.MemberInfo)
                    : entityType.FindProperty(memberIdentity.Name);
                // If unmapped property return null
                if (property == null)
                {
                    return false;
                }

                result = BindProperty(entityProjection, property);
                if (result.Type != type)
                {
                    result = Expression.Convert(result, type);
                }

                return true;
            }

            return false;
        }

        private Expression BindProperty(EntityProjectionExpression entityProjectionExpression, IProperty property)
        {
            return entityProjectionExpression.BindProperty(property);
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            // EF.Property case
            if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var propertyName))
            {
                if (TryBindMember(Visit(source), MemberIdentity.Create(propertyName), methodCallExpression.Type, out var result))
                {
                    return result;
                }

                throw new InvalidOperationException("EF.Property called with wrong property name.");
            }

            // Subquery case
            var subqueryTranslation = _queryableMethodTranslatingExpressionVisitor.TranslateSubquery(methodCallExpression);
            if (subqueryTranslation != null)
            {
                var subquery = (InMemoryQueryExpression)subqueryTranslation.QueryExpression;
                if (subqueryTranslation.ResultCardinality == ResultCardinality.Enumerable)
                {
                    return null;
                }

                subquery.ApplyProjection();
                if (subquery.Projection.Count != 1)
                {
                    return null;
                }

                Expression result;

                // Unwrap ResultEnumerable
                var selectMethod = (MethodCallExpression)subquery.ServerQueryExpression;
                var resultEnumerable = (NewExpression)selectMethod.Arguments[0];
                var resultFunc = ((LambdaExpression)resultEnumerable.Arguments[0]).Body;
                // New ValueBuffer construct
                if (resultFunc is NewExpression newValueBufferExpression)
                {
                    var innerExpression = ((NewArrayExpression)newValueBufferExpression.Arguments[0]).Expressions[0];
                    if (innerExpression is UnaryExpression unaryExpression
                        && innerExpression.NodeType == ExpressionType.Convert
                        && innerExpression.Type == typeof(object))
                    {
                        result = unaryExpression.Operand;
                    }
                    else
                    {
                        result = innerExpression;
                    }

                    return result.Type == methodCallExpression.Type
                        ? result
                        : Expression.Convert(result, methodCallExpression.Type);
                }
                else
                {
                    var selector = (LambdaExpression)selectMethod.Arguments[1];
                    var readValueExpression = ((NewArrayExpression)((NewExpression)selector.Body).Arguments[0]).Expressions[0];
                    if (readValueExpression is UnaryExpression unaryExpression2
                        && unaryExpression2.NodeType == ExpressionType.Convert
                        && unaryExpression2.Type == typeof(object))
                    {
                        readValueExpression = unaryExpression2.Operand;
                    }

                    var valueBufferVariable = Expression.Variable(typeof(ValueBuffer));
                    var replacedReadExpression = ReplacingExpressionVisitor.Replace(
                        selector.Parameters[0],
                        valueBufferVariable,
                        readValueExpression);

                    replacedReadExpression = replacedReadExpression.Type == methodCallExpression.Type
                        ? replacedReadExpression
                        : Expression.Convert(replacedReadExpression, methodCallExpression.Type);

                    return Expression.Block(
                        variables: new[] { valueBufferVariable },
                        Expression.Assign(valueBufferVariable, resultFunc),
                        Expression.Condition(
                            Expression.MakeMemberAccess(valueBufferVariable, _valueBufferIsEmpty),
                            Expression.Default(methodCallExpression.Type),
                            replacedReadExpression));
                }
            }

            // MethodCall translators
            var @object = Visit(methodCallExpression.Object);
            if (TranslationFailed(methodCallExpression.Object, @object))
            {
                return null;
            }

            var arguments = new Expression[methodCallExpression.Arguments.Count];
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = Visit(methodCallExpression.Arguments[i]);
                if (TranslationFailed(methodCallExpression.Arguments[i], argument))
                {
                    return null;
                }
                arguments[i] = argument;
            }

            return methodCallExpression.Update(@object, arguments);
        }

        private static readonly MemberInfo _valueBufferIsEmpty = typeof(ValueBuffer).GetMember(nameof(ValueBuffer.IsEmpty))[0];

        protected override Expression VisitTypeBinary(TypeBinaryExpression typeBinaryExpression)
        {
            if (typeBinaryExpression.NodeType == ExpressionType.TypeIs
                && Visit(typeBinaryExpression.Expression) is EntityProjectionExpression entityProjectionExpression)
            {
                var entityType = entityProjectionExpression.EntityType;

                if (entityType.GetAllBaseTypesInclusive().Any(et => et.ClrType == typeBinaryExpression.TypeOperand))
                {
                    return Expression.Constant(true);
                }

                var derivedType = entityType.GetDerivedTypes().SingleOrDefault(et => et.ClrType == typeBinaryExpression.TypeOperand);
                if (derivedType != null)
                {
                    var discriminatorProperty = entityType.GetDiscriminatorProperty();
                    var boundProperty = BindProperty(entityProjectionExpression, discriminatorProperty);

                    var equals = Expression.Equal(
                        boundProperty,
                        Expression.Constant(derivedType.GetDiscriminatorValue(), discriminatorProperty.ClrType));

                    foreach (var derivedDerivedType in derivedType.GetDerivedTypes())
                    {
                        equals = Expression.OrElse(
                            equals,
                            Expression.Equal(
                                boundProperty,
                                Expression.Constant(derivedDerivedType.GetDiscriminatorValue(), discriminatorProperty.ClrType)));
                    }

                    return equals;
                }
            }

            return Expression.Constant(false);
        }

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            switch (extensionExpression)
            {
                case EntityProjectionExpression _:
                    return extensionExpression;

                case EntityShaperExpression entityShaperExpression:
                    return Visit(entityShaperExpression.ValueBufferExpression);

                case ProjectionBindingExpression projectionBindingExpression:
                    return ((InMemoryQueryExpression)projectionBindingExpression.QueryExpression)
                        .GetMappedProjection(projectionBindingExpression.ProjectionMember);

                case NullConditionalExpression nullConditionalExpression:
                {
                    var translation = Visit(nullConditionalExpression.AccessOperation);

                    return translation.Type == nullConditionalExpression.Type
                        ? translation
                        : Expression.Convert(translation, nullConditionalExpression.Type);
                }

                default:
                    return null;
            }
        }

        protected override Expression VisitListInit(ListInitExpression node) => null;

        protected override Expression VisitInvocation(InvocationExpression node) => null;

        protected override Expression VisitLambda<T>(Expression<T> node) => null;

        protected override Expression VisitParameter(ParameterExpression parameterExpression)
        {
            if (parameterExpression.Name.StartsWith(CompiledQueryParameterPrefix, StringComparison.Ordinal))
            {
                return Expression.Call(
                    _getParameterValueMethodInfo.MakeGenericMethod(parameterExpression.Type),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(parameterExpression.Name));
            }

            throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));
        }

        private static readonly MethodInfo _getParameterValueMethodInfo
            = typeof(InMemoryExpressionTranslatingExpressionVisitor)
                .GetTypeInfo().GetDeclaredMethod(nameof(GetParameterValue));

#pragma warning disable IDE0052 // Remove unread private members
        private static T GetParameterValue<T>(QueryContext queryContext, string parameterName)
#pragma warning restore IDE0052 // Remove unread private members
            => (T)queryContext.ParameterValues[parameterName];

        protected override Expression VisitUnary(UnaryExpression unaryExpression)
        {
            var result = base.VisitUnary(unaryExpression);
            if (result is UnaryExpression outerUnary
                && outerUnary.NodeType == ExpressionType.Convert
                && outerUnary.Operand is UnaryExpression innerUnary
                && innerUnary.NodeType == ExpressionType.Convert)
            {
                var innerMostType = innerUnary.Operand.Type;
                var intermediateType = innerUnary.Type;
                var outerMostType = outerUnary.Type;

                if (outerMostType == innerMostType
                    && intermediateType == innerMostType.UnwrapNullableType())
                {
                    result = innerUnary.Operand;
                }
                else if (outerMostType == typeof(object)
                    && intermediateType == innerMostType.UnwrapNullableType())
                {
                    result = Expression.Convert(innerUnary.Operand, typeof(object));
                }
            }

            return result;
        }

        [DebuggerStepThrough]
        private bool TranslationFailed(Expression original, Expression translation)
            => original != null && (translation == null || translation is EntityProjectionExpression);
    }

}
