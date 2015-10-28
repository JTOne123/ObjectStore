﻿#if DNXCORE50
using ApplicationException = global::System.InvalidOperationException;
#endif

using System;
using System.Collections.Generic;
using System.Text;
using System.Data.SqlClient;
using System.Reflection;
using System.Linq.Expressions;
using System.Linq;
using ObjectStore.Interfaces;

namespace ObjectStore.OrMapping
{
    public partial class InheritensObjectProvider<T> : IObjectProvider
    {
        public class QueryProvider : Expressions.IParseAbleQueryProvider
        {
            #region Membervariablen
            static Dictionary<ValueComparedExpression<Expression>, WeakReference<QueryProvider>> _queryProvider;

            InheritensObjectProvider<T> _objectProvider;
            QueryContext _queryContext;
            WeakCache.ContextEnumerable _enumerable;
            Expression _queryexpression;
            #endregion

            #region Konstruktor
            private QueryProvider(Expression expression, InheritensObjectProvider<T> objectProvider)
            {
                _objectProvider = objectProvider;
                _queryexpression = expression;
            }

            internal static QueryProvider Create(Expression expression, InheritensObjectProvider<T> objectProvider)
            {
                if (_queryProvider == null)
                    _queryProvider = new Dictionary<ValueComparedExpression<Expression>, WeakReference<QueryProvider>>();

                QueryProvider returnValue;
                if (!_queryProvider.ContainsKey(expression))
                    _queryProvider.Add(expression, new WeakReference<QueryProvider>(returnValue = new QueryProvider(expression, objectProvider)));
                else if ((returnValue = _queryProvider[expression].Value) == null)
                    _queryProvider[expression] = new WeakReference<QueryProvider>(returnValue = new QueryProvider(expression, objectProvider));

                return returnValue;
            }
            #endregion

            #region Properties
            internal QueryContext Context
            {
                get
                {
                    return _queryContext == null ? _queryContext = QueryContext.Analyse(_queryexpression) : _queryContext;
                }
            }

            private WeakCache.ContextEnumerable Enumerable
            {
                get
                {
                    return _enumerable ?? (_enumerable = _objectProvider._cache.CreateEnumerable(Context));
                }
            }
            #endregion

            #region Methoden
            public IEnumerable<T> GetValues()
            {
                _objectProvider._dbWorker.FillCache(Context.FillCacheContext);
                return Enumerable;
            }

            public override string ToString()
            {
                try
                {
                    SelectCommandBuilder commandBuilder = _objectProvider._mappingInfoContainer.FillCommand(new SelectCommandBuilder("T"));
                    Context.PrepareSelectCommand(commandBuilder);
                    SqlCommand command = commandBuilder.GetSqlCommand();
                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.AppendFormat("{0};", command.CommandText);
                    foreach (SqlParameter parameter in command.Parameters)
                    {
                        stringBuilder.AppendFormat("{0} = {1},", parameter.ParameterName, parameter.Value);
                    }
                    if (!Context.Load)
                        stringBuilder.Append("(Cache only)");
                    else
                        stringBuilder.Remove(stringBuilder.Length - 1, 1);

                    return stringBuilder.ToString();
                }
                catch (Exception ex)
                {
                    return string.Format("Unparseable:{0}", ex);
                }
            }
            #endregion

            #region Events
            public event System.Collections.Specialized.NotifyCollectionChangedEventHandler CollectionChanged
            {
                add { Enumerable.CollectionChanged += value; }
                remove { Enumerable.CollectionChanged -= value; }
            }
            #endregion

            #region IParseAbleQueryProvider
            public string Parse(Expression expression, Expressions.ParsedExpression parsedExpression)
            {
                if (expression.NodeType == ExpressionType.Call)
                {
                    if (!ValueComparedExpression<Expression>.Equals(_queryexpression, (expression as MethodCallExpression).Arguments[0]))
                    {
                        return (Queryable.Create(_objectProvider, (expression as MethodCallExpression).Arguments[0]).Provider as Expressions.IParseAbleQueryProvider).Parse(expression, parsedExpression);
                    }

                    MethodCallExpression callExpression = expression as MethodCallExpression;

                    #region Any
                    if (callExpression != null &&
                        callExpression.Method.DeclaringType == typeof(System.Linq.Queryable) &&
                        callExpression.Method.Name == "Any")
                    {
                        ExistsCommandBuilder commandBuilder = _objectProvider._mappingInfoContainer.FillCommand(new ExistsCommandBuilder());
                        Context.PrepareSelectCommand(commandBuilder, parsedExpression);
                        return commandBuilder.SubQuery;
                    }
                    #endregion
                    #region Contains
                    if (callExpression != null &&
                        callExpression.Method.DeclaringType == typeof(System.Linq.Queryable) &&
                        callExpression.Method.Name == "Contains" &&
                        callExpression.Arguments.Count == 2 &&
                        callExpression.Arguments[1] is ParameterExpression)
                    {
                        InCommandBuilder commandBuilder = _objectProvider._mappingInfoContainer.FillCommand(new InCommandBuilder(parsedExpression[(ParameterExpression)callExpression.Arguments[1]]));
                        Context.PrepareSelectCommand(commandBuilder, parsedExpression);
                        return commandBuilder.SubQuery;
                    }
                    #endregion

                }

                throw new NotSupportedException(string.Format("Expression '{0}' is not Supported", expression));
            }
            #endregion

            #region IQueryProvider Members

            public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
            {
                if (expression.NodeType == ExpressionType.Call)
                {

                    MethodCallExpression callExpression = (MethodCallExpression)expression;


                    if (callExpression.Method.Name == "Select" && 
                        callExpression.Method.GetParameters().Length == 2 &&
                        callExpression.Method.GetParameters()[0].ParameterType == typeof(IQueryable<T>) &&
                        callExpression.Method.GetParameters()[1].ParameterType == typeof(Expression<Func<T, TElement>>))
                    {
                        Expression<Func<T, TElement>> selector = ((UnaryExpression)callExpression.Arguments[1]).Operand as Expression<Func<T, TElement>>;

                        return new SelectQueryable<TElement, T>(
                            Expression.Lambda<Func<IQueryable<T>>>(callExpression.Arguments[0]).Compile()(),
                            selector.Compile());
                    }


                    if (typeof(T) != typeof(TElement))
                        throw new ApplicationException("Querryable and ObjectProviderType does not match.");


                    Expression[] arguments = new Expression[callExpression.Arguments.Count];

                    bool replaced = false;
                    for (int i = 0; i < callExpression.Arguments.Count; i++)
                    {
                        if (i == 0 && typeof(IQueryable).IsAssignableFrom(callExpression.Arguments[0].Type))
                            arguments[0] = callExpression.Arguments[0];
                        else
                        {
                            arguments[i] = ExpressionHelper.ReplaceExpressionParts(callExpression.Arguments[i],
                                x => x.NodeType == ExpressionType.Constant ||
                                    ExpressionHelper.ContainsAny(x, y => y.NodeType == ExpressionType.Parameter) ?
                                        x :
                                        Expression.Constant(LambdaExpression.Lambda(x).Compile().DynamicInvoke(), x.Type));
                        }

                        replaced = replaced || arguments[i] != callExpression.Arguments[i];
                    }

                    if (replaced)
                        expression = LambdaExpression.Call(callExpression.Object, callExpression.Method, arguments);
                }

                return (IQueryable<TElement>)Queryable.Create(_objectProvider, expression);
            }

            public IQueryable CreateQuery(Expression expression)
            {
                Type genericType = expression.Type.GetGenericArguments()[0];

                return (IQueryable)typeof(QueryProvider).GetMethods().Where(x => x.Name == "CreateQuery" && x.IsGenericMethodDefinition)
                        .First().MakeGenericMethod(genericType).Invoke(this, new object[] { expression });
            }

            public TResult Execute<TResult>(Expression expression)
            {
                if (expression.NodeType == ExpressionType.Call)
                {
                    if (!ValueComparedExpression<Expression>.Equals(_queryexpression, (expression as MethodCallExpression).Arguments[0]))
                    {
                        return Queryable.Create(_objectProvider, (expression as MethodCallExpression).Arguments[0]).Provider.Execute<TResult>(expression);
                    }

                    #region Invoke Save
                    MethodCallExpression callExpression = expression as MethodCallExpression;
                    if (callExpression != null &&
                        callExpression.Method.Name == "Save" &&
                        callExpression.Arguments.Count == 1 &&
                        typeof(TResult) == typeof(bool))
                    {
                        Enumerable.Update(_objectProvider._dbWorker);
                        return (TResult)(object)true;
                    }
                    #endregion
                    #region Invoke Delete
                    if (callExpression != null &&
                        callExpression.Method == typeof(Extensions).GetMethod("Delete").MakeGenericMethod(typeof(T)) &&
                        typeof(TResult) == typeof(bool))
                    {
                        Enumerable.Delete();
                        return (TResult)(object)true;
                    }
                    #endregion
                    #region Invoke BeginFetch
                    if (callExpression != null &&
                            callExpression.Method == typeof(Extensions).GetMethod("BeginFetch").MakeGenericMethod(typeof(T)) &&
                            typeof(TResult) == typeof(IAsyncResult))
                    {
                        WeakCache.ContextEnumerable enumerable = Enumerable; // Um sicherzustellen das die Enumerable auch bereits erstellt wurde.
                        return (TResult)_objectProvider._dbWorker.FillCacheAsync(this);
                    }

                    #endregion
                    #region Invoke DropChanges
                    if (callExpression != null &&
                        callExpression.Method == typeof(Extensions).GetMethod("DropChanges").MakeGenericMethod(typeof(T)) &&
                        typeof(TResult) == typeof(bool))
                    {
                        Enumerable.DropChanges();
                        return (TResult)(object)true;
                    }
                    #endregion
                    #region Invoke FirstOrDefault
                    if (callExpression != null &&
                        callExpression.Method.Name == "FirstOrDefault" &&
                        callExpression.Method.DeclaringType == typeof(System.Linq.Queryable) &&
                        callExpression.Arguments.Count == 1 &&
                        typeof(TResult) == typeof(T))
                    {
                        if (!Context.ForceLoad && Context.OrderExpressions.Count == 0)
                        {
                            TResult returnValue = (TResult)(object)Enumerable.FirstOrDefault();
                            if (returnValue != null)
                                return returnValue;
                        }

                        return (TResult)(object)GetValues().FirstOrDefault();
                    }
                    #endregion
                    #region Invoke Contains
                    if (callExpression != null &&
                        callExpression.Method.Name == "Contains" &&
                        callExpression.Method.DeclaringType == typeof(System.Linq.Queryable) &&
                        callExpression.Arguments.Count == 2 &&
                        typeof(TResult) == typeof(bool))
                    {
                        T element = (T)Expression.Lambda<Func<T>>(callExpression.Arguments[1]).Compile()();
                        return (TResult)(object)(Context.TopCount.HasValue ? Enumerable.Contains(element) : Context.GetPredicateCompiled()(element));
                    }
                    #endregion
                    #region Invoke CheckChanged
                    if (callExpression != null &&
                        callExpression.Method.Name == "CheckChanged" &&
                        callExpression.Method.DeclaringType == typeof(Extensions) &&
                        callExpression.Arguments.Count == 1 &&
                        typeof(TResult) == typeof(bool))
                    {
                        return (TResult)(object)Enumerable.Changed;
                    }
                    #endregion
                    #region Invoke Count
                    if (callExpression != null &&
                        callExpression.Method.Name == "Count" &&
                        callExpression.Method.DeclaringType == typeof(System.Linq.Queryable) &&
                        callExpression.Arguments.Count == 1 &&
                        typeof(TResult) == typeof(int))
                    {
                        return (TResult)(object)GetValues().Count();
                    }
                    #endregion
                    #region Invoke Any
                    if (callExpression != null &&
                        callExpression.Method.Name == "Any" &&
                        callExpression.Method.DeclaringType == typeof(System.Linq.Queryable) &&
                        callExpression.Arguments.Count == 1 &&
                        typeof(TResult) == typeof(bool))
                    {
                        if (Enumerable.Any())
                            return (TResult)(object)true;
                        return (TResult)(object)GetValues().Any();
                    }
                    #endregion

                }

                throw new NotSupportedException("The expression ist not supported by that type of IQueryable.");
            }

            public object Execute(Expression expression)
            {
                return typeof(QueryProvider).GetMethods().Where(x => x.Name == "Execute" && x.IsGenericMethod).FirstOrDefault().MakeGenericMethod(expression.Type).Invoke(this, new object[] { expression });
            }
            #endregion
        }

        internal class QueryContext
        {
            #region OrderItem
            internal interface IOrderItem
            {
                LambdaExpression Expression { get; }
                System.ComponentModel.ListSortDirection Direction { get; }

                IOrderedEnumerable<T> SetOrder(IEnumerable<T> source);
                IOrderedEnumerable<T> SetOrder(IOrderedEnumerable<T> source);
                IOrderedQueryable<T> SetOrder(IQueryable<T> source);
                IOrderedQueryable<T> SetOrder(IOrderedQueryable<T> source);
                int Compare(T first, T second);
            }

            internal class OrderItem<TKey> : IOrderItem
            {
                Expression<Func<T, TKey>> _expression;
                Func<T, TKey> _compiled;
                System.ComponentModel.ListSortDirection _direction;

                public OrderItem(Expression<Func<T, TKey>> expression, System.ComponentModel.ListSortDirection direction)
                {
                    _expression = expression;
                    _direction = direction;
                }

                private Expression<Func<T1, TKey>> ExtendExpression<T1>(Expression<Func<T1, T>> getOrderItem)
                {
                    ParameterExpression paramExpression = LambdaExpression.Parameter(typeof(T1), Expression.Parameters[0].Name);
                    Expression getItemExp = ExpressionHelper.ReplaceExpressionParts(getOrderItem.Body, a => a != getOrderItem.Parameters[0] ? a : paramExpression);
                    return LambdaExpression.Lambda<Func<T1, TKey>>(ExpressionHelper.ReplaceExpressionParts(Expression.Body, a => a != Expression.Parameters[0] ? a : getItemExp), paramExpression);
                }

                private Func<T, TKey> Compiled
                {
                    get
                    {
                        return _compiled == null ? _compiled =
                            ((Expression<Func<T, TKey>>)ExpressionHelper.ReplaceExpressionParts(_expression, x =>
                            {
                                if (x.NodeType != ExpressionType.MemberAccess ||
                                    ((MemberExpression)x).Expression.NodeType != ExpressionType.MemberAccess ||
                                    ((MemberExpression)((MemberExpression)x).Expression).Expression.NodeType != ExpressionType.Parameter)
                                    return x;
                                else
                                    return System.Linq.Expressions.Expression.Condition(
                                        System.Linq.Expressions.Expression.Equal(
                                            ((MemberExpression)x).Expression,
                                            System.Linq.Expressions.Expression.Constant(null)),
                                        System.Linq.Expressions.Expression.Constant(
#if DNXCORE50
                                            x.Type.GetTypeInfo().IsValueType ? 
#else
                                            x.Type.IsValueType ? 
#endif
                                                Activator.CreateInstance(x.Type) : null, x.Type),
                                        x);
                            })).Compile() : _compiled;
                    }
                }

                #region IOrderItem Members

                public LambdaExpression Expression
                {
                    get
                    {
                        return _expression;
                    }
                }

                public System.ComponentModel.ListSortDirection Direction { get { return _direction; } }

                public IOrderedEnumerable<T> SetOrder(IEnumerable<T> source)
                {
                    if (_direction == System.ComponentModel.ListSortDirection.Ascending)
                        return source.OrderBy(_expression.Compile());
                    else
                        return source.OrderByDescending(_expression.Compile());
                }

                public IOrderedEnumerable<T> SetOrder(IOrderedEnumerable<T> source)
                {
                    if (_direction == System.ComponentModel.ListSortDirection.Ascending)
                        return source.ThenBy(_expression.Compile());
                    else
                        return source.ThenByDescending(_expression.Compile());
                }

                public IOrderedQueryable<T> SetOrder(IQueryable<T> source)
                {
                    if (_direction == System.ComponentModel.ListSortDirection.Ascending)
                        return source.OrderBy(_expression);
                    else
                        return source.OrderByDescending(_expression);
                }

                public IOrderedQueryable<T> SetOrder(IOrderedQueryable<T> source)
                {
                    if (_direction == System.ComponentModel.ListSortDirection.Ascending)
                        return source.ThenBy(_expression);
                    else
                        return source.ThenByDescending(_expression);
                }

                public IOrderedEnumerable<T1> SetOrder<T1>(IEnumerable<T1> source, Expression<Func<T1, T>> getOrderItem)
                {
                    if (_direction == System.ComponentModel.ListSortDirection.Ascending)
                        return source.OrderBy(ExtendExpression(getOrderItem).Compile());
                    else
                        return source.OrderByDescending(ExtendExpression(getOrderItem).Compile());
                }

                public IOrderedEnumerable<T1> SetOrder<T1>(IOrderedEnumerable<T1> source, Expression<Func<T1, T>> getOrderItem)
                {
                    if (_direction == System.ComponentModel.ListSortDirection.Ascending)
                        return source.ThenBy(ExtendExpression(getOrderItem).Compile());
                    else
                        return source.ThenByDescending(ExtendExpression(getOrderItem).Compile());
                }

                public int Compare(T first, T second)
                {
                    return _direction == System.ComponentModel.ListSortDirection.Ascending ?
                        Comparer<TKey>.Default.Compare(Compiled(first), Compiled(second)) :
                        Comparer<TKey>.Default.Compare(Compiled(first), Compiled(second)) * -1;
                }
                #endregion
            }
            #endregion

            #region Membervariablen
            List<Expression<Func<T, bool>>> _whereExpressions;
            List<IOrderItem> _orderExpressions;
            int? _topCount;
            LoadBehavior _loadBehavior;
            bool _loaded;
            List<string> _predicateRelatedProperties;
            bool _forceCache;
            bool _forceLoad;
            bool _isCached;
            Func<T, bool> _compiledPredicate = null;

            QueryContext _innerQueryContext;

            static Dictionary<ValueComparedExpression<Expression>, WeakReference<QueryContext>> _cachedContext = new Dictionary<ValueComparedExpression<Expression>, WeakReference<QueryContext>>();
            static QueryContext _baseQueryContext;
            #endregion

            #region Konstruktor
            private QueryContext(QueryContext innerQueryContext)
            {
                _whereExpressions = new List<Expression<Func<T, bool>>>();
                _loadBehavior = MappingInfo.GetMappingInfo(typeof(T)).LoadBehavior;
                _loaded = false;
                _isCached = false;
                if (innerQueryContext != null)
                {
                    _innerQueryContext = innerQueryContext;
                    _topCount = _innerQueryContext._topCount;
                    _forceCache = _innerQueryContext._forceCache;
                    _forceLoad = _innerQueryContext._forceLoad;
                    _orderExpressions = _innerQueryContext._orderExpressions == null ? null : new List<IOrderItem>(_innerQueryContext._orderExpressions);
                }
            }
            #endregion

            #region Methoden
            #region Static
            internal static QueryContext Analyse(Expression expression)
            {
                return AnalyseInternal(expression).AddToCache(expression);
            }

            private static QueryContext AnalyseInternal(Expression expression)
            {
                if (_cachedContext.ContainsKey(expression))
                {
                    QueryContext context = _cachedContext[expression].Value;
                    if (context != null)
                        return context;
                    else
                        _cachedContext.Remove(expression);
                }

                if (expression.NodeType == ExpressionType.Constant &&
                    (expression.Type == typeof(Queryable) || ((ConstantExpression)expression).Value is Queryable))
                {
                    return BaseContext;
                }

                if (expression.NodeType == ExpressionType.Call)
                {
                    MethodCallExpression callExpression = expression as MethodCallExpression;
                    QueryContext context =
                        callExpression.Arguments.Count > 0 ? AnalyseInternal(callExpression.Arguments[0]) :
                        callExpression.Object != null ? AnalyseInternal(callExpression.Object) :
                        BaseContext;

                    if (context._isCached)
                        context = new QueryContext(context);

                    #region Invoke Where
                    if (callExpression.Method.Name == "Where" &&
                       callExpression.Arguments.Count == 2 &&
                       callExpression.Arguments[1].Type == typeof(Expression<Func<T, bool>>))
                    {
                        Expression<Func<T, bool>> predicate = Expression.Lambda<Func<Expression<Func<T, bool>>>>(callExpression.Arguments[1]).Compile().Invoke();
                        context.SetWhere(predicate);
                        return context;
                    }
                    #endregion

                    #region Invoke OrderBy
                    if (callExpression.Method.Name == "OrderBy" &&
                            callExpression.Arguments.Count == 2)
                    {
                        LambdaExpression orderBy = Expression.Lambda<Func<LambdaExpression>>(callExpression.Arguments[1]).Compile().Invoke();
                        typeof(QueryContext).GetMethod("SetOrderBy", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(orderBy.Body.Type).Invoke(context, new object[] { orderBy, System.ComponentModel.ListSortDirection.Ascending });
                        return context;
                    }
                    #endregion

                    #region Invoke OrderByDescending
                    if (callExpression.Method.Name == "OrderByDescending" &&
                            callExpression.Arguments.Count == 2)
                    {
                        LambdaExpression orderBy = Expression.Lambda<Func<LambdaExpression>>(callExpression.Arguments[1]).Compile().Invoke();
                        typeof(QueryContext).GetMethod("SetOrderBy", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(orderBy.Body.Type).Invoke(context, new object[] { orderBy, System.ComponentModel.ListSortDirection.Descending });
                        return context;
                    }
                    #endregion

                    #region Invoke ThenBy
                    if (callExpression.Method.Name == "ThenBy" &&
                            callExpression.Arguments.Count == 2)
                    {
                        LambdaExpression orderBy = Expression.Lambda<Func<LambdaExpression>>(callExpression.Arguments[1]).Compile().Invoke();
                        typeof(QueryContext).GetMethod("SetThenBy", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(orderBy.Body.Type).Invoke(context, new object[] { orderBy, System.ComponentModel.ListSortDirection.Ascending });
                        return context;
                    }
                    #endregion

                    #region Invoke ThenByDescending
                    if (callExpression.Method.Name == "ThenByDescending" &&
                            callExpression.Arguments.Count == 2)
                    {
                        LambdaExpression orderBy = Expression.Lambda<Func<LambdaExpression>>(callExpression.Arguments[1]).Compile().Invoke();
                        typeof(QueryContext).GetMethod("SetThenBy", BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(orderBy.Body.Type).Invoke(context, new object[] { orderBy, System.ComponentModel.ListSortDirection.Descending });
                        return context;
                    }
                    #endregion

                    #region Invoke Take
                    if (callExpression.Method.Name == "Take" &&
                       callExpression.Arguments.Count == 2 &&
                       callExpression.Arguments[1].Type == typeof(int))
                    {
                        int count = Expression.Lambda<Func<int>>(callExpression.Arguments[1]).Compile().Invoke();
                        context.SetTop(count);
                        return context;
                    }
                    #endregion

                    #region Invoke Force
                    if (callExpression.Method.Name == "ForceLoad" &&
                       callExpression.Arguments.Count == 1)
                    {
                        if (context._innerQueryContext._forceLoad)
                            return context._innerQueryContext;

                        context.SetForceLoad();
                        return context;
                    }
                    #endregion

                    #region Invoke ForceCache
                    if (callExpression.Method.Name == "ForceCache" &&
                       callExpression.Arguments.Count == 1)
                    {
                        if (context._innerQueryContext._forceCache)
                            return context._innerQueryContext;

                        context.SetForceCache();
                        return context;
                    }
                    #endregion
                }

                throw new NotSupportedException();
            }

            private QueryContext AddToCache(ValueComparedExpression<Expression> expression)
            {
                if (_isCached)
                    return this;

                if (!_cachedContext.ContainsKey(expression))
                    _cachedContext.Add(expression, new WeakReference<QueryContext>(this));
                else
                    _cachedContext[expression] = new WeakReference<QueryContext>(this);

                _isCached = true;
                return this;
            }
            #endregion

            #region Protected
            protected void SetWhere(Expression<Func<T, bool>> predicate)
            {
                _whereExpressions.Add((Expression<Func<T, bool>>)ExpressionHelper.ReplaceExpressionParts(predicate, x => ExpressionHelper.ContainsAny(x, y => y.NodeType == ExpressionType.Parameter) || x == predicate ? x : Expression.Constant(LambdaExpression.Lambda(x).Compile().DynamicInvoke(), x.Type)));
            }

            protected void SetOrderBy<TKey>(Expression<Func<T, TKey>> orderBy, System.ComponentModel.ListSortDirection direction)
            {
                _orderExpressions = new List<IOrderItem>();
                _orderExpressions.Add(new OrderItem<TKey>(orderBy, direction));
            }

            protected void SetThenBy<TKey>(Expression<Func<T, TKey>> thenBy, System.ComponentModel.ListSortDirection direction)
            {
                if (_orderExpressions == null)
                {
                    throw new ApplicationException("OrderBy muss be befor ThenBy");
                }
                _orderExpressions.Add(new OrderItem<TKey>(thenBy, direction));
            }

            protected void SetTop(int count)
            {
                _topCount = count;
            }

            protected virtual void SetForceCache()
            {
                _forceCache = true;
            }

            protected virtual void SetForceLoad()
            {
                _forceLoad = true;
            }
            #endregion

            #region Public
            public Expression<Func<T, bool>> GetPredicate()
            {
                Expression<Func<T, bool>> _predicate = null; // = _whereExpressions.First();
                foreach (Expression<Func<T, bool>> expression in WhereExpressions)
                {
                    _predicate = _predicate == null ? expression : ExpressionHelper.CombineExpressions(_predicate, expression);
                }
                return _predicate;
            }

            public Func<T, bool> GetPredicateCompiled()
            {
                if (_compiledPredicate != null)
                    return _compiledPredicate;

                Expression<Func<T, bool>> predicate = GetPredicate();

                predicate = predicate == null ? null : (Expression<Func<T, bool>>)ExpressionHelper.ReplaceExpressionParts(predicate, x =>
                    {
                        if (x == predicate || !(x is BinaryExpression) || x.Type != typeof(bool) || x.NodeType == ExpressionType.And || x.NodeType == ExpressionType.Or || x.NodeType == ExpressionType.ExclusiveOr || x.NodeType == ExpressionType.AndAlso || x.NodeType == ExpressionType.OrElse)
                            return x;

                        BinaryExpression binaryExpression = (BinaryExpression)x;
                        PropertyInfo propertyInfo = null;
                        ParameterExpression paramExpersion = null;
                        if (GetMemberAccessParams(binaryExpression.Left, ref propertyInfo, ref paramExpersion) ||
                            GetMemberAccessParams(binaryExpression.Right, ref propertyInfo, ref paramExpersion))
                        {
                            return Expression.AndAlso(Expression.NotEqual(Expression.Property(paramExpersion, propertyInfo), Expression.Constant(null)), binaryExpression);
                        }

                        return x;

                    });

                return _compiledPredicate = predicate != null ? predicate.Compile() : x => true;
            }

            bool GetMemberAccessParams(Expression expression, ref PropertyInfo propertyInfo, ref ParameterExpression paramExpression)
            {
                if (expression.NodeType == ExpressionType.Convert)
                    expression = ((UnaryExpression)expression).Operand;

                if (expression.NodeType == ExpressionType.MemberAccess &&
                    ((MemberExpression)expression).Expression.NodeType == ExpressionType.MemberAccess &&
#if DNXCORE50
                    !((MemberExpression)expression).Expression.Type.GetTypeInfo().IsValueType &&
#else
                    !((MemberExpression)expression).Expression.Type.IsValueType &&
#endif
                    ((MemberExpression)((MemberExpression)expression).Expression).Member is PropertyInfo &&
                    ((MemberExpression)((MemberExpression)expression).Expression).Expression.NodeType == ExpressionType.Parameter)
                {
                    propertyInfo = ((MemberExpression)((MemberExpression)expression).Expression).Member as PropertyInfo;
                    paramExpression = ((MemberExpression)((MemberExpression)expression).Expression).Expression as ParameterExpression;
                    return true;
                }
                return false;
            }

            public virtual void PrepareSelectCommand(IModifyableCommandBuilder commandBuilder)
            {
                PrepareSelectCommand(commandBuilder, null);
            }

            public virtual void PrepareSelectCommand(IModifyableCommandBuilder commandBuilder, Expressions.ParsedExpression parentParsedExpression)
            {
                #region WhereExpressions
                if (WhereExpressions.Any())
                {
                    bool first = true;
                    foreach (Expression<Func<T, bool>> predicate in WhereExpressions)
                    {
                        Expressions.ParsedExpression parsedExpression;
                        if (parentParsedExpression == null)
                        {
                            parsedExpression = Expressions.ParsedExpression.ParseExpression(predicate, obj =>
                            {
                                if (obj is DateTime)
                                    if ((DateTime)obj < new DateTime(1753, 1, 1))
                                        obj = new DateTime(1753, 1, 1);
                                    else if((DateTime) obj > new DateTime(9999,12,31))
                                        obj = new DateTime(9999, 12, 31);

                                SqlParameter parameter = new SqlParameter(string.Format("@param{0}", ObjectStoreManager.CurrentUniqe()), obj);
                                commandBuilder.Parameters.Add(parameter);
                                return parameter;
                            });
                        }
                        else
                        {
                            parsedExpression = Expressions.ParsedExpression.ParseExpression(predicate, null, parentParsedExpression) as Expressions.ParsedExpression;
                        }

                        parsedExpression[predicate.Parameters[0]] = commandBuilder.Alias;

                        if (first)
                        {
                            if (_whereExpressions.Count > 1)
                                commandBuilder.WhereClausel = "(" + parsedExpression.SqlExpression + ")";
                            else
                                commandBuilder.WhereClausel = parsedExpression.SqlExpression;

                            first = false;
                        }
                        else
                        {
                            commandBuilder.WhereClausel = string.Format(" {0} AND ({1}) ", commandBuilder.WhereClausel, parsedExpression.SqlExpression);
                        }
                        foreach (Expressions.ParsedExpression.Join join in parsedExpression.Joins)
                            commandBuilder.AddJoin(string.Format("{0} {1}", join.Table, join.Alias), string.Format("{1}.{0} = {2}", join.Field, join.FieldAlias, join.ForeignField));
                    }
                }
                #endregion

                #region OrderByExpressions
                if (_orderExpressions != null)
                {
                    foreach (IOrderItem orderItem in _orderExpressions)
                    {
                        Expressions.ParsedExpression parsedExpression = Expressions.ParsedExpression.ParseExpression(orderItem.Expression, obj =>
                        {
                            SqlParameter parameter = new SqlParameter(string.Format("@param{0}", commandBuilder.Parameters.Count), obj);
                            commandBuilder.Parameters.Add(parameter);
                            return parameter;
                        });

                        parsedExpression[orderItem.Expression.Parameters[0]] = commandBuilder.Alias;

                        commandBuilder.SetOrderBy(string.Format(orderItem.Direction == System.ComponentModel.ListSortDirection.Ascending ? "{0}" : "{0} DESC", parsedExpression.SqlExpression));
                        foreach (Expressions.ParsedExpression.Join join in parsedExpression.Joins)
                            commandBuilder.AddJoin(string.Format("{0} {1}", join.Table, join.Alias), string.Format("{1}.{0} = {2}", join.Field, join.FieldAlias, join.ForeignField));
                    }
                }
                #endregion

                #region TopClausel
                if (_topCount.HasValue)
                {
                    commandBuilder.SetTop(_topCount.Value);
                }
                #endregion
            }

            public virtual void SetLoaded()
            {
                _loaded = true;
            }

            public int Compare(T first, T second)
            {
                if (_orderExpressions == null || _orderExpressions.Count == 0 ||
                    (first == null && second == null))
                    return 0;

                if (first == null)
                    return -1;

                if (second == null)
                    return 1;

                foreach (IOrderItem item in _orderExpressions)
                {
                    int value = item.Compare(first, second);
                    if (value != 0)
                        return value;
                }
                return 0;
            }
            #endregion
            #endregion

            #region Properties
            private IEnumerable<Expression<Func<T, bool>>> WhereExpressions
            {
                get
                {
                    if (_innerQueryContext == null)
                        return _whereExpressions;
                    else if (_whereExpressions.Count == 0)
                        return _innerQueryContext.WhereExpressions;
                    else
                        return _innerQueryContext.WhereExpressions.Union(_whereExpressions);
                }
            }

            public System.Collections.ObjectModel.ReadOnlyCollection<IOrderItem> OrderExpressions
            {
                get
                {
                    return _orderExpressions == null ? new List<IOrderItem>().AsReadOnly() : _orderExpressions.AsReadOnly();
                }
            }

            public IEnumerable<string> PredicateRelatedProperties
            {
                get
                {
                    if (_predicateRelatedProperties != null)
                        return _predicateRelatedProperties.AsReadOnly();

                    if (_whereExpressions == null || _whereExpressions.Count == 0)
                        return _innerQueryContext == null ? 
                            (_predicateRelatedProperties = new List<string>(MappingInfo.GetMappingInfo(typeof(T)).KeyMappingInfos.Select(x => x.MemberInfo.Name))).AsReadOnly() : 
                            (_predicateRelatedProperties = new List<string>(_innerQueryContext.PredicateRelatedProperties)).AsReadOnly();

                    foreach (Expression<Func<T, bool>> expression in _whereExpressions)
                    {
                        IEnumerable<string> enumerable =
                                ExpressionHelper.GetFilteredExpression(expression,
                                    x => x.NodeType == ExpressionType.MemberAccess &&
#if DNXCORE50
                            (x as MemberExpression).Member is PropertyInfo)
#else
                            (x as MemberExpression).Member.MemberType == MemberTypes.Property)
#endif
                                            .Select(x => (x as MemberExpression).Member.Name);
                        if (_predicateRelatedProperties == null)
                            _predicateRelatedProperties = enumerable.ToList();
                        else
                            _predicateRelatedProperties.AddRange(enumerable);
                    }

                    if (_innerQueryContext != null)
                        _predicateRelatedProperties.AddRange(_innerQueryContext.PredicateRelatedProperties);

                    _predicateRelatedProperties = _predicateRelatedProperties.Distinct().ToList();
                    return _predicateRelatedProperties.AsReadOnly();
                }
            }

            public int? TopCount
            {
                get
                {
                    return _topCount;
                }
            }

            public bool Load
            {
                get
                {
                    if (_forceLoad)
                        return true;

                    if (_forceCache)
                        return false;

                    if (_loaded)
                        return false;

                    return _innerQueryContext == null ? true : _innerQueryContext.Load;
                }
            }

            public bool ForceLoad
            {
                get
                {
                    return _forceLoad;
                }
            }

            public QueryContext FillCacheContext
            {
                get
                {
                    return _loadBehavior != LoadBehavior.OnFirstAccessFullLoad ? this : BaseContext.Load || !Load ? BaseContext : this;
                }
            }

            public static QueryContext BaseContext
            {
                get
                {
                    if (_baseQueryContext == null)
                        _baseQueryContext = new QueryContext(null).AddToCache(Expression.Constant(ObjectStoreManager.DefaultObjectStore.GetQueryable<T>()));

                    return _baseQueryContext;
                }
            }
            #endregion
        }
    }
}