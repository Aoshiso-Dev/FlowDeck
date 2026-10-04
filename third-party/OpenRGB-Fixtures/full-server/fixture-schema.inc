    json                detection_settings_schema;

    detection_settings_schema["hid_safe_mode"]["title"]                     = QT_TRANSLATE_NOOP("Settings", "HID Safe Mode");
    detection_settings_schema["hid_safe_mode"]["type"]                      = "bool";
    detection_settings_schema["hid_safe_mode"]["description"]               = QT_TRANSLATE_NOOP("Settings", "Use an alternate method for detecting HID devices");

    detection_settings_schema["initial_detection_delay_ms"]["title"]        = QT_TRANSLATE_NOOP("Settings", "Initial Detection Delay (ms)");
    detection_settings_schema["initial_detection_delay_ms"]["type"]         = "integer";
    detection_settings_schema["initial_detection_delay_ms"]["description"]  = QT_TRANSLATE_NOOP("Settings", "Amount of time, in milliseconds, to wait before detecting devices when started");

    settings_manager->RegisterSettingsSchemaIgnoreFilter("Detectors", QT_TRANSLATE_NOOP("Settings", "Detection"), detection_settings_schema);

    /*-----------------------------------------------------*\
    | Create LogManager settings schema                     |
    \*-----------------------------------------------------*/
    json                logmanager_settings_schema;

    logmanager_settings_schema["log_console"]["title"]                      = QT_TRANSLATE_NOOP("Settings", "Enable Log Console");
    logmanager_settings_schema["log_console"]["type"]                       = "bool";

    logmanager_settings_schema["log_file"]["title"]                         = QT_TRANSLATE_NOOP("Settings", "Enable Log File");
    logmanager_settings_schema["log_file"]["type"]                          = "bool";
    logmanager_settings_schema["log_file"]["default"]                       = true;

    logmanager_settings_schema["loglevel"]["title"]                         = QT_TRANSLATE_NOOP("Settings", "Log Level");
    logmanager_settings_schema["loglevel"]["type"]                          = "integer";
    logmanager_settings_schema["loglevel"]["default"]                       = 3;
    logmanager_settings_schema["loglevel"]["enum"][0]                       = 0;
    logmanager_settings_schema["loglevel"]["enumNames"][0]                  = "Fatal";
    logmanager_settings_schema["loglevel"]["enum"][1]                       = 1;
    logmanager_settings_schema["loglevel"]["enumNames"][1]                  = "Error";
    logmanager_settings_schema["loglevel"]["enum"][2]                       = 2;
    logmanager_settings_schema["loglevel"]["enumNames"][2]                  = "Warning";
    logmanager_settings_schema["loglevel"]["enum"][3]                       = 3;
    logmanager_settings_schema["loglevel"]["enumNames"][3]                  = "Info";
    logmanager_settings_schema["loglevel"]["enum"][4]                       = 4;
    logmanager_settings_schema["loglevel"]["enumNames"][4]                  = "Verbose";
    logmanager_settings_schema["loglevel"]["enum"][5]                       = 5;
    logmanager_settings_schema["loglevel"]["enumNames"][5]                  = "Debug";
    logmanager_settings_schema["loglevel"]["enum"][6]                       = 6;
    logmanager_settings_schema["loglevel"]["enumNames"][6]                  = "Trace";

    logmanager_settings_schema["file_count_limit"]["title"]                 = QT_TRANSLATE_NOOP("Settings", "Log File Count Limit");
    logmanager_settings_schema["file_count_limit"]["type"]                  = "integer";
    logmanager_settings_schema["file_count_limit"]["description"]           = QT_TRANSLATE_NOOP("Settings", "Maximum number of log files to keep, 0 for no limit");
    logmanager_settings_schema["file_count_limit"]["default"]               = 10;
    logmanager_settings_schema["file_count_limit"]["minimum"]               = 0;

    settings_manager->RegisterSettingsSchema("LogManager", QT_TRANSLATE_NOOP("Settings", "Log Manager"), logmanager_settings_schema);

    /*-----------------------------------------------------*\
    | Create Server settings schema                         |
    \*-----------------------------------------------------*/
    json                server_settings_schema;

    server_settings_schema["all_controllers"]["title"]                      = QT_TRANSLATE_NOOP("Settings", "Serve All Controllers");
    server_settings_schema["all_controllers"]["type"]                       = "bool";
    server_settings_schema["all_controllers"]["description"]                = QT_TRANSLATE_NOOP("Settings", "Include controllers provided by client connections and plugins");

    server_settings_schema["default_host"]["title"]                         = QT_TRANSLATE_NOOP("Settings", "Default Host");
    server_settings_schema["default_host"]["type"]                          = "string";
    server_settings_schema["default_host"]["default"]                       = OPENRGB_SDK_HOST;

    server_settings_schema["default_port"]["title"]                         = QT_TRANSLATE_NOOP("Settings", "Default Port");
    server_settings_schema["default_port"]["type"]                          = "integer";
    server_settings_schema["default_port"]["default"]                       = OPENRGB_SDK_PORT;
    server_settings_schema["default_port"]["minimum"]                       = 0;
    server_settings_schema["default_port"]["maximum"]                       = 65535;

    server_settings_schema["legacy_workaround"]["title"]                    = QT_TRANSLATE_NOOP("Settings", "Legacy Workaround");
    server_settings_schema["legacy_workaround"]["type"]                     = "bool";
    server_settings_schema["legacy_workaround"]["description"]              = QT_TRANSLATE_NOOP("Settings", "Workaround for some older SDK implementations that sent incorrect packet size for certain packets");

    settings_manager->RegisterSettingsSchemaLocalOnly("Server", QT_TRANSLATE_NOOP("Settings", "Server"), server_settings_schema);

