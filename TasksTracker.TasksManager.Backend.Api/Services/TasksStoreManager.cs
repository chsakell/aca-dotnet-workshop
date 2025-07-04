using Dapr.Client;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using TasksTracker.TasksManager.Backend.Api.Models;

namespace TasksTracker.TasksManager.Backend.Api.Services
{
    public class TasksStoreManager : ITasksManager
    {
        private static string STORE_NAME = "statestore";
        private readonly DaprClient _daprClient;
        private readonly IConfiguration _config;
        private readonly ILogger<TasksStoreManager> _logger;

        public TasksStoreManager(DaprClient daprClient, IConfiguration config, ILogger<TasksStoreManager> logger)
        {
            _daprClient = daprClient;
            _config = config;
            _logger = logger;
        }
        public async Task<Guid> CreateNewTask(string taskName, string createdBy, string assignedTo, DateTime dueDate)
        {
            var taskModel = new TaskModel()
            {
                TaskId = Guid.NewGuid(),
                TaskName = taskName,
                TaskCreatedBy = createdBy,
                TaskCreatedOn = DateTime.UtcNow,
                TaskDueDate = dueDate,
                TaskAssignedTo = assignedTo,
            };

            _logger.LogInformation("Save a new task with name: '{0}' to state store", taskModel.TaskName);
            await _daprClient.SaveStateAsync<TaskModel>(STORE_NAME, taskModel.TaskId.ToString(), taskModel);
            await PublishTaskSavedEvent(taskModel);
            return taskModel.TaskId;
        }

        public async Task<bool> DeleteTask(Guid taskId)
        {
            _logger.LogInformation("Delete task with Id: '{0}'", taskId);
            await _daprClient.DeleteStateAsync(STORE_NAME, taskId.ToString());
            return true;
        }

        public async Task<TaskModel?> GetTaskById(Guid taskId)
        {
            _logger.LogInformation("Getting task with Id: '{0}'", taskId);
            var taskModel = await _daprClient.GetStateAsync<TaskModel>(STORE_NAME, taskId.ToString());
            return taskModel;
        }

        public async Task<List<TaskModel>> GetTasksByCreator(string createdBy)
        {
            var query = "{" +
                    "\"filter\": {" +
                        "\"EQ\": { \"taskCreatedBy\": \"" + createdBy + "\" }" +
                    "}}";

            var queryResponse = await _daprClient.QueryStateAsync<TaskModel>(STORE_NAME, query);

            var tasksList = queryResponse.Results
                .Where(q => q.Data != null)         // filter null data
                .Select(q => q.Data!)
                .OrderByDescending(o=>o.TaskCreatedOn);

            return tasksList.ToList();
        }

        public async Task<bool> MarkTaskCompleted(Guid taskId)
        {
            _logger.LogInformation("Mark task with Id: '{0}' as completed", taskId);
            var taskModel = await _daprClient.GetStateAsync<TaskModel>(STORE_NAME, taskId.ToString());
            if (taskModel != null)
            {
                taskModel.IsCompleted = true;
                await _daprClient.SaveStateAsync<TaskModel>(STORE_NAME, taskModel.TaskId.ToString(), taskModel);
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateTask(Guid taskId, string taskName, string assignedTo, DateTime dueDate)
        {
            _logger.LogInformation("Update task with Id: '{0}'", taskId);
            var taskModel = await _daprClient.GetStateAsync<TaskModel>(STORE_NAME, taskId.ToString());
            var currentAssignee = taskModel.TaskAssignedTo;
            if (taskModel != null)
            {
                taskModel.TaskName = taskName;
                taskModel.TaskAssignedTo = assignedTo;
                taskModel.TaskDueDate = dueDate;
                await _daprClient.SaveStateAsync<TaskModel>(STORE_NAME, taskModel.TaskId.ToString(), taskModel);
                if (!taskModel.TaskAssignedTo.Equals(currentAssignee, StringComparison.OrdinalIgnoreCase))
                {
                    await PublishTaskSavedEvent(taskModel);
                }
                return true;
            }
            return false;
        }

        public async Task<List<TaskModel>> GetYesterdaysDueTasks()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters =
                {
                    new JsonStringEnumConverter(),
                    new DateTimeConverter("yyyy-MM-ddTHH:mm:ss")
                },
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var yesterday = DateTime.Today.AddDays(-1);

            var jsonDate = JsonSerializer.Serialize(yesterday, options);

            _logger.LogInformation("Getting overdue tasks for yesterday date: '{0}'", jsonDate);

            var query = "{" +
                    "\"filter\": {" +
                        "\"EQ\": { \"taskDueDate\": " + jsonDate + " }" +
                    "}}";

            var queryResponse = await _daprClient.QueryStateAsync<TaskModel>(STORE_NAME, query);

            var tasksList = queryResponse.Results
                             .Where(q => q.Data != null)         // filter null data
                             .Select(q => q.Data)
                             .Where(q => q!.IsCompleted == false && q.IsOverDue == false)
                             .OrderBy(o => o!.TaskCreatedOn);

            return tasksList.ToList()!;
        }

        public async Task MarkOverdueTasks(List<TaskModel> overDueTasksList)
        {
            foreach (var taskModel in overDueTasksList)
            {
                _logger.LogInformation("Mark task with Id: '{0}' as OverDue task", taskModel.TaskId);
                taskModel.IsOverDue = true;
                await _daprClient.SaveStateAsync<TaskModel>(STORE_NAME, taskModel.TaskId.ToString(), taskModel);
            }
        }

        private async Task PublishTaskSavedEvent(TaskModel taskModel)
        {
            _logger.LogInformation("Publish Task Saved event for task with Id: '{0}' and Name: '{1}' for Assignee: '{2}'",
            taskModel.TaskId, taskModel.TaskName, taskModel.TaskAssignedTo);
            await _daprClient.PublishEventAsync("dapr-pubsub-servicebus", "tasksavedtopic", taskModel);
        }
    }
}