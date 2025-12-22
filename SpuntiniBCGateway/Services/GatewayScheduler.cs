using System.Text.Json;
using SpuntiniBCGateway.Services;

public class GatewayScheduler
{
    private readonly IConfiguration _config;
    private readonly EventLog _logger;
    private List<ScheduleEntry> _schedules = new();
    private Dictionary<string, DateTime> _lastExecutionTimes = new();
    private string _scheduleFilePath = string.Empty;

    public GatewayScheduler(IConfiguration config, EventLog logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _logger.InfoAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Start scheduler initialisation");
            await LoadSchedulesAsync();
            await _logger.InfoAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Scheduler initialized with {_schedules.Count} schedule(s).");
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync(nameof(GatewayScheduler), "GatewayScheduler", ex);
        }
    }

    private async Task LoadSchedulesAsync()
    {
        _schedules.Clear();
        _lastExecutionTimes.Clear();

        _scheduleFilePath = _config["ScheduleFilePath"] ?? "gateway-schedule.json";

        if (!File.Exists(_scheduleFilePath))
        {
            await _logger.WarningAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Schedule file not found at {_scheduleFilePath}");
            return;
        }

        try
        {
            string jsonContent = await File.ReadAllTextAsync(_scheduleFilePath);
            using JsonDocument doc = JsonDocument.Parse(jsonContent);
            var schedules = doc.RootElement.GetProperty("schedules");

            foreach (var scheduleElement in schedules.EnumerateArray())
            {
                var entry = new ScheduleEntry
                {
                    Id = scheduleElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Company = scheduleElement.TryGetProperty("company", out var company) ? company.GetString() ?? "" : "",
                    Mode = scheduleElement.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "" : "",
                    IntervalMinutes = scheduleElement.TryGetProperty("intervalMinutes", out var interval) ? interval.GetInt32() : 0,
                    Enabled = !scheduleElement.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
                    StartTime = scheduleElement.TryGetProperty("startTime", out var startTime) ? startTime.GetString() : null,
                    EndTime = scheduleElement.TryGetProperty("endTime", out var endTime) ? endTime.GetString() : null,
                    RepeatPattern = scheduleElement.TryGetProperty("repeatPattern", out var repeat) ? repeat.GetString() ?? "once" : "once",
                    RepeatEveryHours = scheduleElement.TryGetProperty("repeatEveryHours", out var repeatHours) ? repeatHours.GetInt32() : 0,
                    RepeatEveryMinutes = scheduleElement.TryGetProperty("repeatEveryMinutes", out var repeatMinutes) ? repeatMinutes.GetInt32() : 0,
                    Weekdays = LoadWeekdays(scheduleElement),
                    LastRan = LoadLastRanTime(scheduleElement),
                    LastChecked = LoadLastCheckedTime(scheduleElement)
                };

                if (!string.IsNullOrWhiteSpace(entry.Company) && !string.IsNullOrWhiteSpace(entry.Mode))
                {
                    try
                    {
                        ValidateScheduleEntry(entry);
                        _schedules.Add(entry);
                    }
                    catch (InvalidOperationException ex)
                    {
                        await _logger.WarningAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Skipping invalid schedule: {ex.Message}");
                    }
                }
            }

            await _logger.InfoAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Loaded {_schedules.Count} schedule(s) from {_scheduleFilePath}");
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync(nameof(GatewayScheduler), "GatewayScheduler", ex);
        }
    }

    private List<DayOfWeek> LoadWeekdays(JsonElement scheduleElement)
    {
        var weekdays = new List<DayOfWeek>();
        if (scheduleElement.TryGetProperty("weekdays", out var weekdayArray))
        {
            foreach (var day in weekdayArray.EnumerateArray())
            {
                if (day.ValueKind == JsonValueKind.String)
                {
                    string dayStr = day.GetString() ?? "";
                    if (Enum.TryParse<DayOfWeek>(dayStr, ignoreCase: true, out var parsedDay))
                    {
                        weekdays.Add(parsedDay);
                    }
                }
            }
        }
        return weekdays;
    }

    private DateTime? LoadLastRanTime(JsonElement scheduleElement)
    {
        if (scheduleElement.TryGetProperty("lastRan", out var lastRan) && lastRan.ValueKind == JsonValueKind.String)
        {
            string dateStr = lastRan.GetString() ?? "";
            if (DateTime.TryParse(dateStr, out var parsedDate))
            {
                return parsedDate;
            }
        }
        return null;
    }

    private DateTime? LoadLastCheckedTime(JsonElement scheduleElement)
    {
        if (scheduleElement.TryGetProperty("lastChecked", out var lastChecked) && lastChecked.ValueKind == JsonValueKind.String)
        {
            string dateStr = lastChecked.GetString() ?? "";
            if (DateTime.TryParse(dateStr, out var parsedDate))
            {
                return parsedDate;
            }
        }
        return null;
    }

    private void ValidateScheduleEntry(ScheduleEntry entry)
    {
        // Validate that endTime is after startTime if both are specified
        if (!string.IsNullOrWhiteSpace(entry.StartTime) && !string.IsNullOrWhiteSpace(entry.EndTime))
        {
            if (TimeOnly.TryParse(entry.StartTime, out var startTime) && 
                TimeOnly.TryParse(entry.EndTime, out var endTime))
            {
                if (endTime <= startTime)
                {
                    throw new InvalidOperationException(
                        $"Schedule '{entry.Id}': endTime ({entry.EndTime}) must be after startTime ({entry.StartTime})");
                }
            }
        }
    }

    public async Task CheckAndExecuteSchedulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTime checkTime = DateTime.Now;
            foreach (var schedule in _schedules.Where(s => s.Enabled))
            {
                // Record when this schedule was checked
                schedule.LastChecked = checkTime;
                
                if (ShouldExecute(schedule))
                {
                    await ExecuteScheduleAsync(schedule, cancellationToken);
                    DateTime executionTime = DateTime.Now;
                    _lastExecutionTimes[schedule.Id] = executionTime;
                    schedule.LastRan = executionTime;
                    await UpdateScheduleFileAsync(schedule);
                }
                else if (!_lastExecutionTimes.ContainsKey(schedule.Id) || 
                         DateTime.Now.Subtract(_lastExecutionTimes[schedule.Id]).TotalMinutes > 5)
                {
                    // Update lastChecked periodically even if task doesn't run
                    // (every 5 minutes) to avoid excessive file writes
                    await UpdateScheduleFileAsync(schedule);
                }
            }
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync(nameof(GatewayScheduler), "GatewayScheduler", ex);
        }
    }

    private bool ShouldExecute(ScheduleEntry schedule)
    {
        // Check if current time is within the allowed window
        if (!IsWithinTimeWindow(schedule))
        {
            // Task is outside of allowed execution window - skip it
            return false;
        }

        // Check weekday restrictions
        if (schedule.Weekdays.Count > 0 && !schedule.Weekdays.Contains(DateTime.Now.DayOfWeek))
        {
            // Task is not scheduled for today's weekday
            return false;
        }

        // Check based on repeat pattern
        return CheckRepeatPattern(schedule);
    }

    private bool IsWithinTimeWindow(ScheduleEntry schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.StartTime) && string.IsNullOrWhiteSpace(schedule.EndTime))
            return true;

        DateTime now = DateTime.Now;
        TimeOnly currentTime = TimeOnly.FromDateTime(now);

        if (!string.IsNullOrWhiteSpace(schedule.StartTime) && TimeOnly.TryParse(schedule.StartTime, out var startTime))
        {
            if (currentTime < startTime)
                return false;
        }

        if (!string.IsNullOrWhiteSpace(schedule.EndTime) && TimeOnly.TryParse(schedule.EndTime, out var endTime))
        {
            if (currentTime >= endTime)
                return false;
        }

        return true;
    }

    private bool CheckRepeatPattern(ScheduleEntry schedule)
    {
        if (!_lastExecutionTimes.TryGetValue(schedule.Id, out var lastExecution))
        {
            // First execution - check if we should run based on pattern
            return ShouldRunOnSchedule(schedule, lastExecution: null);
        }

        return ShouldRunOnSchedule(schedule, lastExecution);
    }

    private bool ShouldRunOnSchedule(ScheduleEntry schedule, DateTime? lastExecution)
    {
        DateTime now = DateTime.Now;

        return schedule.RepeatPattern.ToLower() switch
        {
            "once" => lastExecution == null && MatchesStartTime(schedule),
            
            "minutes" => lastExecution == null
                ? MatchesStartTime(schedule)
                : now.Subtract(lastExecution.Value).TotalMinutes >= (schedule.RepeatEveryMinutes > 0 ? schedule.RepeatEveryMinutes : 1),
            
            "daily" => lastExecution == null 
                ? MatchesStartTime(schedule)
                : now.Date > lastExecution.Value.Date && MatchesStartTime(schedule),
            
            "hourly" => lastExecution == null
                ? MatchesStartTime(schedule)
                : now.Subtract(lastExecution.Value).TotalMinutes >= (schedule.RepeatEveryHours > 0 ? schedule.RepeatEveryHours * 60 : 60),
            
            "weekly" => lastExecution == null
                ? MatchesStartTime(schedule)
                : now.Subtract(lastExecution.Value).TotalDays >= 7 && MatchesStartTime(schedule),
            
            "monthly" => lastExecution == null
                ? MatchesStartTime(schedule)
                : (now.Month != lastExecution.Value.Month || now.Year != lastExecution.Value.Year) && MatchesStartTime(schedule),
            
            _ => lastExecution == null && schedule.IntervalMinutes > 0 && MatchesStartTime(schedule)
        };
    }

    private bool MatchesStartTime(ScheduleEntry schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.StartTime))
            return true;

        if (!TimeOnly.TryParse(schedule.StartTime, out var startTime))
            return true;

        TimeOnly currentTime = TimeOnly.FromDateTime(DateTime.Now);
        // Allow a 1-minute tolerance window for execution
        return currentTime.Ticks - startTime.Ticks > 0;
    }

    private async Task ExecuteScheduleAsync(ScheduleEntry schedule, CancellationToken cancellationToken)
    {
        try
        {
            // Acquire the execution lock to prevent concurrent gateway execution from any source
            if (!await Program._gatewayExecutionLock.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                await _logger.WarningAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Scheduled task {schedule.Id} skipped - gateway is already running from another source.");
                return;
            }

            try
            {
                await _logger.InfoAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Executing scheduled task: {schedule.Id} (Company: {schedule.Company}, Mode: {schedule.Mode})");

                var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["company"] = schedule.Company.ToUpperInvariant(),
                    ["mode"] = schedule.Mode
                };

                var childConfig = new ConfigurationBuilder()
                    .AddConfiguration(_config)
                    .AddInMemoryCollection(overrides.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)))
                    .Build();

                var logger = new EventLog(childConfig);

                await Program.RunGatewayAsync(childConfig, logger, cancellationToken).ConfigureAwait(false);

                await _logger.InfoAsync(nameof(GatewayScheduler), "GatewayScheduler", $"Scheduled task {schedule.Id} completed successfully.");
            }
            finally
            {
                Program._gatewayExecutionLock.Release();
            }
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync(nameof(GatewayScheduler), "GatewayScheduler", ex);
        }
    }

    private async Task UpdateScheduleFileAsync(ScheduleEntry schedule)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_scheduleFilePath) || !File.Exists(_scheduleFilePath))
                return;

            string jsonContent = await File.ReadAllTextAsync(_scheduleFilePath);
            using JsonDocument doc = JsonDocument.Parse(jsonContent);
            
            var schedules = doc.RootElement.GetProperty("schedules").EnumerateArray().ToList();
            var scheduleList = new List<JsonElement>(schedules);
            
            // Find and update the schedule entry
            for (int i = 0; i < scheduleList.Count; i++)
            {
                if (scheduleList[i].TryGetProperty("id", out var idProperty) && 
                    idProperty.GetString() == schedule.Id)
                {
                    // Recreate the schedule object with updated lastRan and lastChecked
                    var updatedSchedule = new
                    {
                        id = schedule.Id,
                        company = schedule.Company,
                        mode = schedule.Mode,
                        intervalMinutes = schedule.IntervalMinutes,
                        enabled = schedule.Enabled,
                        startTime = schedule.StartTime,
                        endTime = schedule.EndTime,
                        repeatPattern = schedule.RepeatPattern,
                        repeatEveryHours = schedule.RepeatEveryHours,
                        weekdays = schedule.Weekdays.Select(w => w.ToString()).ToList(),
                        lastRan = schedule.LastRan?.ToString("O"), // ISO 8601 format
                        lastChecked = schedule.LastChecked?.ToString("O"), // ISO 8601 format
                        repeatEveryMinutes = schedule.RepeatEveryMinutes
                    };

                    // Build the complete schedules array
                    var allSchedules = new List<object>();
                    for (int j = 0; j < scheduleList.Count; j++)
                    {
                        if (j == i)
                        {
                            allSchedules.Add(updatedSchedule);
                        }
                        else
                        {
                            var deserializedObj = JsonSerializer.Deserialize<object>(scheduleList[j].GetRawText());
                            if (deserializedObj != null)
                                allSchedules.Add(deserializedObj);
                        }
                    }

                    var root = new { schedules = allSchedules };
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string updatedJson = JsonSerializer.Serialize(root, options);
                    await File.WriteAllTextAsync(_scheduleFilePath, updatedJson);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync(nameof(GatewayScheduler), "GatewayScheduler", ex);
        }
    }

    public class ScheduleEntry
    {
        public string Id { get; set; } = "";
        public string Company { get; set; } = "";
        public string Mode { get; set; } = "";
        public int IntervalMinutes { get; set; } = 0;
        public bool Enabled { get; set; } = true;
        public string? StartTime { get; set; } // HH:mm format
        public string? EndTime { get; set; } // HH:mm format
        public string RepeatPattern { get; set; } = "once"; // once, minutes, daily, hourly, weekly, monthly
        public int RepeatEveryHours { get; set; } = 0; // For hourly pattern: repeat every X hours
        public int RepeatEveryMinutes { get; set; } = 0; // For minutes pattern: repeat every X minutes
        public List<DayOfWeek> Weekdays { get; set; } = new(); // For weekly scheduling: specific weekdays
        public DateTime? LastRan { get; set; } // Last execution timestamp
        public DateTime? LastChecked { get; set; } // Last time scheduler checked if this task needed to run
    }
}

