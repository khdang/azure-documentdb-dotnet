﻿namespace DocumentDBBenchmark
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.ComponentModel;
    using System.Configuration;
    using System.Diagnostics;
    using System.Dynamic;
    using System.Net;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Text.RegularExpressions;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Microsoft.Azure.Documents.Linq;
    using Microsoft.Azure.Documents.Partitioning;
    using Newtonsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// This sample demonstrates how to achieve high performance writes using DocumentDB.
    /// </summary>
    public sealed class Program
    {
        private static readonly string DatabaseName = ConfigurationManager.AppSettings["DatabaseName"];
        private static readonly string DataCollectionName = ConfigurationManager.AppSettings["CollectionName"];
        private static readonly string MetricCollectionName = ConfigurationManager.AppSettings["MetricCollectionName"];
        private static readonly int CollectionThroughput = int.Parse(ConfigurationManager.AppSettings["CollectionThroughput"]);

        private static readonly ConnectionPolicy ConnectionPolicy = new ConnectionPolicy { ConnectionMode = ConnectionMode.Gateway, ConnectionProtocol = Protocol.Https, RequestTimeout = new TimeSpan(1, 0, 0) };

        private static readonly int TaskCount = int.Parse(ConfigurationManager.AppSettings["DegreeOfParallelism"]);
        private static readonly int DefaultConnectionLimit = int.Parse(ConfigurationManager.AppSettings["DegreeOfParallelism"]);
        private static readonly string InstanceId = Dns.GetHostEntry("LocalHost").HostName + Process.GetCurrentProcess().Id;
        private const int MinThreadPoolSize = 100;

        private int pendingTaskCount;
        private long documentsInserted;
        private ConcurrentDictionary<int, double> requestUnitsConsumed = new ConcurrentDictionary<int, double>();
        private DocumentClient client;

        /// <summary>
        /// Initializes a new instance of the <see cref="Program"/> class.
        /// </summary>
        /// <param name="client">The DocumentDB client instance.</param>
        private Program(DocumentClient client)
        {
            this.client = client;
        }

        /// <summary>
        /// Main method for the sample.
        /// </summary>
        /// <param name="args">command line arguments.</param>
        public static void Main(string[] args)
        {
            ServicePointManager.UseNagleAlgorithm = true;
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.DefaultConnectionLimit = DefaultConnectionLimit;
            ThreadPool.SetMinThreads(MinThreadPoolSize, MinThreadPoolSize);

            string endpoint = ConfigurationManager.AppSettings["EndPointUrl"];
            string authKey = ConfigurationManager.AppSettings["AuthorizationKey"];

            Console.WriteLine("Summary:");
            Console.WriteLine("--------------------------------------------------------------------- ");
            Console.WriteLine("Endpoint: {0}", endpoint);
            Console.WriteLine("Collection : {0}.{1} at {2} request units per second", DatabaseName, DataCollectionName, ConfigurationManager.AppSettings["CollectionThroughput"]);
            Console.WriteLine("Document Template*: {0}", ConfigurationManager.AppSettings["DocumentTemplateFile"]);
            Console.WriteLine("Degree of parallelism*: {0}", TaskCount);
            Console.WriteLine("--------------------------------------------------------------------- ");
            Console.WriteLine();

            Console.WriteLine("DocumentDBBenchmark starting...");

            try
            {
                using (var client = new DocumentClient(
                    new Uri(endpoint),
                    authKey,
                    ConnectionPolicy))
                {
                    var program = new Program(client);
                    program.RunAsync().Wait();
                    Console.WriteLine("DocumentDBBenchmark completed successfully.");
                }
            }

#if !DEBUG
            catch (Exception e)
            {
                // If the Exception is a DocumentClientException, the "StatusCode" value might help identity 
                // the source of the problem. 
                Console.WriteLine("Samples failed with exception:{0}", e);
            }
#endif

            finally
            {
            }
        }

        /// <summary>
        /// Run samples for Order By queries.
        /// </summary>
        /// <returns>a Task object.</returns>
        private async Task RunAsync()
        {
            DocumentCollection dataCollection = GetCollectionIfExists(DatabaseName, DataCollectionName);

            if (bool.Parse(ConfigurationManager.AppSettings["ShouldCleanupOnStart"]) || dataCollection == null)
            {
                Database database = GetDatabaseIfExists(DatabaseName);
                if (database != null)
                {
                    await client.DeleteDatabaseAsync(database.SelfLink);
                }

                Console.WriteLine("Creating database {0}", DatabaseName);
                database = await client.CreateDatabaseAsync(new Database { Id = DatabaseName });

                Console.WriteLine("Creating collection {0} with {1} RU/s", DataCollectionName, CollectionThroughput);
                dataCollection = await this.CreatePartitionedCollectionAsync(DatabaseName, DataCollectionName);
            }
            else
            {
                OfferV2 offer = (OfferV2)client.CreateOfferQuery().Where(o => o.ResourceLink == dataCollection.SelfLink).AsEnumerable().FirstOrDefault();
                int throughput = offer.Content.OfferThroughput;
                Console.WriteLine("Found collection {0} with {1} RU/s", DataCollectionName, CollectionThroughput);
            }

            DocumentCollection metricCollection = GetCollectionIfExists(DatabaseName, MetricCollectionName);

            // Configure to expire metrics for old clients if not updated for longer than a minute
            int defaultTimeToLive = 60;

            if (metricCollection == null)
            {
                Console.WriteLine("Creating metric collection {0}", MetricCollectionName);
                DocumentCollection metricCollectionDefinition = new DocumentCollection();
                metricCollectionDefinition.Id = MetricCollectionName;
                metricCollectionDefinition.DefaultTimeToLive = defaultTimeToLive;

                metricCollection = await ExecuteWithRetries<ResourceResponse<DocumentCollection>>(
                   this.client,
                   () => client.CreateDocumentCollectionAsync(
                       UriFactory.CreateDatabaseUri(DatabaseName),
                       new DocumentCollection { Id = MetricCollectionName },
                       new RequestOptions { OfferThroughput = 5000 }), 
                   true);
            }
            else
            {
                metricCollection.DefaultTimeToLive = defaultTimeToLive;
                await client.ReplaceDocumentCollectionAsync(metricCollection);
            }

            Console.WriteLine("Starting Inserts with {0} tasks", TaskCount);
            string sampleDocument = File.ReadAllText(ConfigurationManager.AppSettings["DocumentTemplateFile"]);

            pendingTaskCount = TaskCount;
            var tasks = new List<Task>();
            tasks.Add(this.LogOutputStats());

            long numberOfDocumentsToInsert = long.Parse(ConfigurationManager.AppSettings["NumberOfDocumentsToInsert"])/TaskCount;
            for (var i = 0; i < TaskCount; i++)
            {
                tasks.Add(this.InsertDocument(i, client, dataCollection, sampleDocument, numberOfDocumentsToInsert));
            }

            await Task.WhenAll(tasks);

            if (bool.Parse(ConfigurationManager.AppSettings["ShouldCleanupOnFinish"]))
            {
                Console.WriteLine("Deleting Database {0}", DatabaseName);
                await ExecuteWithRetries<ResourceResponse<Database>>(
                   this.client,
                   () => client.DeleteDatabaseAsync(UriFactory.CreateDatabaseUri(DatabaseName)),
                   true);
            }
        }

        private async Task InsertDocument(int taskId, DocumentClient client, DocumentCollection collection, string sampleJson, long numberOfDocumentsToInsert)
        {
            requestUnitsConsumed[taskId] = 0;
            string partitionKeyProperty = collection.PartitionKey.Paths[0].Replace("/", "");
            Dictionary<string, object> newDictionary = JsonConvert.DeserializeObject<Dictionary<string, object>>(sampleJson);

            for (var i = 0; i < numberOfDocumentsToInsert; i++)
            {
                newDictionary["id"] = Guid.NewGuid().ToString();
                newDictionary[partitionKeyProperty] = Guid.NewGuid().ToString();

                try
                {
                    ResourceResponse<Document> response = await ExecuteWithRetries<ResourceResponse<Document>>(
                        client,
                        () => client.CreateDocumentAsync(
                            UriFactory.CreateDocumentCollectionUri(DatabaseName, DataCollectionName),
                            newDictionary,
                            new RequestOptions() { }));

                    string partition = response.SessionToken.Split(':')[0];
                    requestUnitsConsumed[taskId] += response.RequestCharge;
                    Interlocked.Increment(ref this.documentsInserted);
                }
                catch (Exception e)
                {
                    Trace.TraceError("Failed to write {0}. Exception was {1}", JsonConvert.SerializeObject(newDictionary), e);
                }
            }

            Interlocked.Decrement(ref this.pendingTaskCount);
        }

        private async Task LogOutputStats()
        {
            long lastCount = 0;
            double lastRequestUnits = 0;
            double lastSeconds = 0;
            double requestUnits = 0;
            double ruPerSecond = 0;
            double ruPerMonth = 0;

            Stopwatch watch = new Stopwatch();
            watch.Start();

            while (this.pendingTaskCount > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                double seconds = watch.Elapsed.TotalSeconds;

                requestUnits = 0;
                foreach (int taskId in requestUnitsConsumed.Keys)
                {
                    requestUnits += requestUnitsConsumed[taskId];
                }

                long currentCount = this.documentsInserted;
                ruPerSecond = (requestUnits / seconds);
                ruPerMonth = ruPerSecond * 86400 * 30;

                Console.WriteLine("Inserted {0} docs @ {1} writes/s, {2} RU/s ({3}B max monthly 1KB reads)",
                    currentCount,
                    Math.Round(this.documentsInserted / seconds),
                    Math.Round(ruPerSecond),
                    Math.Round(ruPerMonth / (1000 * 1000 * 1000)));

                Dictionary<string, object> latestStats = new Dictionary<string, object>();
                latestStats["id"] = string.Format("latest{0}", InstanceId);
                latestStats["type"] = "latest";
                latestStats["totalDocumentsCreated"] = currentCount;
                latestStats["documentsCreatedPerSecond"] = Math.Round(this.documentsInserted / seconds);
                latestStats["requestUnitsPerSecond"] = Math.Round(ruPerSecond);
                latestStats["requestUnitsPerMonth"] = Math.Round(ruPerSecond) * 86400 * 30;
                latestStats["documentsCreatedInLastSecond"] = Math.Round((currentCount - lastCount) / (seconds - lastSeconds));
                latestStats["requestUnitsInLastSecond"] = Math.Round((requestUnits - lastRequestUnits) / (seconds - lastSeconds));
                latestStats["requestUnitsPerMonthBasedOnLastSecond"] =
                    Math.Round(((requestUnits - lastRequestUnits) / (seconds - lastSeconds)) * 86400 * 30);

                await InsertMetricsToDocumentDB(latestStats);

                lastCount = documentsInserted;
                lastSeconds = seconds;
                lastRequestUnits = requestUnits;
            }

            double totalSeconds = watch.Elapsed.TotalSeconds;
            ruPerSecond = (requestUnits / totalSeconds);
            ruPerMonth = ruPerSecond * 86400 * 30;

            Console.WriteLine();
            Console.WriteLine("Summary:");
            Console.WriteLine("--------------------------------------------------------------------- ");
            Console.WriteLine("Inserted {0} docs @ {1} writes/s, {2} RU/s ({3}B max monthly 1KB reads)",
                lastCount,
                Math.Round(this.documentsInserted / watch.Elapsed.TotalSeconds),
                Math.Round(ruPerSecond),
                Math.Round(ruPerMonth / (1000 * 1000 * 1000)));
            Console.WriteLine("--------------------------------------------------------------------- ");
        }

        private async Task InsertMetricsToDocumentDB(Dictionary<string, object> latestStats)
        {
            try
            {
                await ExecuteWithRetries<ResourceResponse<Document>>(
                    client,
                    () => client.UpsertDocumentAsync(
                        UriFactory.CreateDocumentCollectionUri(DatabaseName, MetricCollectionName),
                        latestStats));
            }
            catch (Exception e)
            {
                Trace.TraceError("Insert metrics document failed with {0}", e);
            }
        }

        /// <summary>
        /// Create a partitioned collection.
        /// </summary>
        /// <returns>The created collection.</returns>
        private async Task<DocumentCollection> CreatePartitionedCollectionAsync(string databaseName, string collectionName)
        {
            DocumentCollection existingCollection = GetCollectionIfExists(databaseName, collectionName);

            DocumentCollection collection = new DocumentCollection();
            collection.Id = collectionName;
            collection.PartitionKey.Paths.Add(ConfigurationManager.AppSettings["CollectionPartitionKey"]);

            // Show user cost of running this test
            double estimatedCostPerMonth = 0.06 * CollectionThroughput;
            double estimatedCostPerHour = estimatedCostPerMonth / (24 * 30);
            Console.WriteLine("The collection will cost an estimated ${0} per hour (${1} per month)", estimatedCostPerHour, estimatedCostPerMonth);
            Console.WriteLine("Press enter to continue ...");
            Console.ReadLine();

            return await ExecuteWithRetries<ResourceResponse<DocumentCollection>>(
                this.client,
                () => client.CreateDocumentCollectionAsync(
                    UriFactory.CreateDatabaseUri(databaseName), 
                    collection, 
                    new RequestOptions { OfferThroughput = CollectionThroughput }));
        }

        /// <summary>
        /// Get the database if it exists, null if it doesn't
        /// </summary>
        /// <returns>The requested database</returns>
        private Database GetDatabaseIfExists(string databaseName)
        {
            return client.CreateDatabaseQuery().Where(d => d.Id == databaseName).AsEnumerable().FirstOrDefault();
        }

        /// <summary>
        /// Get the collection if it exists, null if it doesn't
        /// </summary>
        /// <returns>The requested collection</returns>
        private DocumentCollection GetCollectionIfExists(string databaseName, string collectionName)
        {
            if (GetDatabaseIfExists(databaseName) == null)
            {
                return null;
            }

            return client.CreateDocumentCollectionQuery(UriFactory.CreateDatabaseUri(databaseName))
                .Where(c => c.Id == collectionName).AsEnumerable().FirstOrDefault();
        }

        /// <summary>
        /// Execute the function with retries on throttle.
        /// </summary>
        /// <typeparam name="V">The type of return value from the execution.</typeparam>
        /// <param name="client">The DocumentDB client instance.</param>
        /// <param name="function">The function to execute.</param>
        /// <returns>The response from the execution.</returns>
        public static async Task<V> ExecuteWithRetries<V>(DocumentClient client, Func<Task<V>> function, bool shouldLogRetries = false)
        {
            TimeSpan sleepTime = TimeSpan.Zero;
            int[] expectedStatusCodes = new int[] { 429, 400, 503 };

            while (true)
            {
                try
                {
                    return await function();
                }
                catch (System.Net.Http.HttpRequestException)
                {
                    sleepTime = TimeSpan.FromSeconds(1);
                }
                catch (Exception e)
                {
                    DocumentClientException de;
                    if (!TryExtractDocumentClientException(e, out de))
                    {
                        throw;
                    }

                    sleepTime = de.RetryAfter;
                    if (shouldLogRetries)
                    {
                        Console.WriteLine("Retrying after sleeping for {0}", sleepTime);
                    }
                }

                await Task.Delay(sleepTime);
            }
        }

        private static bool TryExtractDocumentClientException(Exception e, out DocumentClientException de)
        {
            if (e is DocumentClientException)
            {
                de = (DocumentClientException)e;
                return true;
            }

            if (e is AggregateException)
            {
                if (e.InnerException is DocumentClientException)
                {
                    de = (DocumentClientException)e.InnerException;
                    return true;
                }
            }

            de = null;
            return false;
        }
    }
}
