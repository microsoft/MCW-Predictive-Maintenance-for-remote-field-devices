using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Azure.EventHubs;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using RestSharp;
using Microsoft.WindowsAzure.Storage.Table;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Fabrikam.Oil.Pumps
{
    public static class PumpFailurePrediction
    {
        [FunctionName("PumpFailurePrediction")]
        public static async Task Run([EventHubTrigger("iot-central-feed", Connection = "fabrikam-oil_RootManageSharedAccessKey_EVENTHUB", 
                                ConsumerGroup = "ingressprocessing")] EventData[] events, ILogger log)
        {
            var exceptions = new List<Exception>();
            var randomGenerator = new Random();
            foreach (EventData eventData in events)
            {
                try
                {
                    //deserialize message body into the Telemetry object
                    var telemetry = JsonConvert.DeserializeObject<Telemetry>(
                                Encoding.UTF8.GetString(eventData.Body.Array, eventData.Body.Offset, eventData.Body.Count));
                    var deviceId = (string)eventData.SystemProperties["iothub-connection-device-id"];                     

                    var restClient = new RestClient(Environment.GetEnvironmentVariable("PredictionModelEndpoint"));
                    //temp - path 1 returns 1, path 0 returns 0
                    var path = randomGenerator.Next(0,2);
                    var modelRequest = new RestRequest(path.ToString());
                    modelRequest.AddJsonBody(telemetry);
                    var modelResult = restClient.ExecuteAsPost<int>(modelRequest,"POST");
                    if(modelResult.Content == "1") 
                    {
                        // impending failure detected from model - send notification 

                        //check when the device had its last notification   
                        var cloudStorageAccount =  CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));                    
                        var table = cloudStorageAccount.CreateCloudTableClient().GetTableReference("DeviceNotifications");

                        var retrieveEntityOp = TableOperation.Retrieve<DeviceNotification>("Devices", deviceId);
                        var entity = (DeviceNotification)(await table.ExecuteAsync(retrieveEntityOp)).Result;
                        bool isNewEntity = false;
                        if(entity == null) 
                        {
                            isNewEntity = true;
                            entity = new DeviceNotification(deviceId);
                            entity.LastNotificationUtc = DateTime.UtcNow;
                        }

                        var timeSpan = DateTime.UtcNow - entity.LastNotificationUtc;
                        if(isNewEntity || timeSpan.Hours > 24)
                        {
                            //if it has been greater than 24 hours - update the notification timestamp
                            entity.LastNotificationUtc = DateTime.UtcNow;
                            var replaceEntityOp = TableOperation.InsertOrReplace(entity);
                            await table.ExecuteAsync(replaceEntityOp);

                            //notify workforce via Microsoft Flow triggered by queue entry
                            var queue = cloudStorageAccount.CreateCloudQueueClient().GetQueueReference("flownotificationqueue");
                            StringBuilder message = new StringBuilder(deviceId +" has been flagged as requiring maintenance by the ");
                                    message.Append("predictive maintenance system. ");
                                    message.Append("Please visit this pump and return it to normal operating parameters.");
                            var queueMessage = new CloudQueueMessage(message.ToString());
                            await queue.AddMessageAsync(queueMessage);
                            log.LogInformation($"Notification email for {deviceId} queued for delivery");
                        }
                    }                    
                }
                catch (Exception e)
                {
                    // We need to keep processing the rest of the batch - capture this exception and continue.
                    // Also, consider capturing details of the message that failed processing so it can be processed again later.
                    exceptions.Add(e);
                }
            }

            // Once processing of the batch is complete, if any messages in the batch failed processing throw an exception so that 
            // there is a record of the failure.

            if (exceptions.Count > 1)
                throw new AggregateException(exceptions);

            if (exceptions.Count == 1)
                throw exceptions.Single();
        }
    }
}