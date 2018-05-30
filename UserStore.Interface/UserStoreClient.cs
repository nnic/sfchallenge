﻿using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Communication.Client;
using Microsoft.ServiceFabric.Services.Remoting.Client;

namespace UserStore.Interface
{
    /// <summary>
    /// A partition aware UserStore client. Distributes the users evenly across all partitions.
    /// </summary>
    public class UserStoreClient : IUserStore
    {
        private readonly ServiceProxyFactory _serviceProxyFactory;
        private readonly Uri _userStoreServiceUri;
        public UserStoreClient()
        {
            var settings = new OperationRetrySettings(
                maxRetryBackoffIntervalOnNonTransientErrors: TimeSpan.FromSeconds(2.0), 
                maxRetryBackoffIntervalOnTransientErrors: TimeSpan.FromSeconds(2.0), 
                defaultMaxRetryCount: 3);

            _serviceProxyFactory = new ServiceProxyFactory(settings);

            var serviceUriBuilder = new ServiceUriBuilder("UserStore");
            _userStoreServiceUri = serviceUriBuilder.ToUri();
        }

        public async Task<string> AddUserAsync(User user, CancellationToken cancellationToken)
        {
            var userStoreProxy = GetUserStoreProxy(user);

            return await userStoreProxy.AddUserAsync(user, cancellationToken);
        }

        /// <summary>
        /// Retrieves all users from all partitions. Potentially a lot of users! Not something you would normally do.
        /// </summary>
        /// <returns></returns>
        public async Task<List<User>> GetUsersAsync()
        {
            var users = new List<User>();

            using (var client = new FabricClient())
            {
                var partitions = await client.QueryManager.GetPartitionListAsync(_userStoreServiceUri);
                var tasks = new List<Task<List<User>>>();
                foreach (var partition in partitions)
                {
                    tasks.Add(GetUsersInPartition(partition));
                }
                var result = await Task.WhenAll(tasks);
                for (var i = 0; i < result.Length; i++)
                {
                    users = users.Concat(result[i]).ToList();
                }
            }

            return users;
        }

        private async Task<List<User>> GetUsersInPartition(Partition partition)
        {
            var partitionInformation = (Int64RangePartitionInformation)partition.PartitionInformation;
            var userStoreProxy = _serviceProxyFactory.CreateServiceProxy<IUserStore>(_userStoreServiceUri, new ServicePartitionKey(partitionInformation.LowKey));
            var usersInPartition = await userStoreProxy.GetUsersAsync();
            return usersInPartition;
        }

        public async Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken)
        {
            var userStoreProxy = GetUserStoreProxy(userId);

            return await userStoreProxy.DeleteUserAsync(userId, cancellationToken);
        }

        public async Task<User> GetUserAsync(string userId)
        {
            var userStoreProxy = GetUserStoreProxy(userId);

            return await userStoreProxy.GetUserAsync(userId);
        }

        public async Task<bool> UpdateUserAsync(User user, CancellationToken cancellationToken)
        {
            var userStoreProxy = GetUserStoreProxy(user);

            return await userStoreProxy.UpdateUserAsync(user, cancellationToken);
        }
        private IUserStore GetUserStoreProxy(string userId)
        {
            var servicePartition = GetServicePartition(userId);
            return _serviceProxyFactory.CreateServiceProxy<IUserStore>(_userStoreServiceUri, servicePartition);
        }

        private IUserStore GetUserStoreProxy(User user)
        {
            var servicePartition = GetServicePartition(user);
            return _serviceProxyFactory.CreateServiceProxy<IUserStore>(_userStoreServiceUri, servicePartition);
        }

        private ServicePartitionKey GetServicePartition(User user)
        {
            return GetServicePartition(user.Id);
        }

        private ServicePartitionKey GetServicePartition(string userId)
        {
            var hash = Hash(userId);
            return new ServicePartitionKey(hash);
        }

        /// <summary>
        /// Generates a FNV hash (non-cryptographic) based on a string with evenly distributed output. 
        /// </summary>
        private long Hash(string input)
        {
            input = input.ToUpperInvariant();
            var value = Encoding.UTF8.GetBytes(input);
            ulong hash = 14695981039346656037;
            for (int i = 0; i < value.Length; ++i)
            {
                hash ^= value[i];
                hash *= 1099511628211;
            }
            return (long)hash;
        }
    }
}