﻿using ObjectStore.OrMapping;
using ObjectStore.Interfaces;
using Microsoft.Extensions.DependencyInjection;


namespace ObjectStore
{
    public static class ObjectStoreExtensions
    {
        public static void AddObjectStore(this IServiceCollection services, IDataBaseProvider databaseProvider, string connectionString)
        {
            RelationalObjectStore relationalObjectStore = new RelationalObjectStore(connectionString, databaseProvider, true);
            ObjectStoreManager.DefaultObjectStore.RegisterObjectProvider(relationalObjectStore);
            services.Add(new ServiceDescriptor(typeof(IObjectProvider), relationalObjectStore));
        }
    }
}
