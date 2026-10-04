#pragma once
#include <memory>
#include <string>
#include <vector>
#include <stdexcept>
struct LogMessage { unsigned int level, line, timestamp; std::string filename, text; };
using PLogMessage = std::shared_ptr<LogMessage>;
class LogManager
{
public:
    static LogManager* get() { static LogManager instance; return &instance; }
    void ClearLogBuffer() { throw std::runtime_error("unexpected log API"); }
    std::vector<PLogMessage> GetLogBuffer() { throw std::runtime_error("unexpected log API"); }
    unsigned int GetLogLevel() { return 2; }
    void SetLogLevel(unsigned int) { throw std::runtime_error("unexpected log API"); }
};
#define LOG_FATAL(...) ((void)0)
#define LOG_ERROR(...) ((void)0)
#define LOG_WARNING(...) ((void)0)
#define LOG_INFO(...) ((void)0)
#define LOG_VERBOSE(...) ((void)0)
#define LOG_DEBUG(...) ((void)0)
#define LOG_TRACE(...) ((void)0)
