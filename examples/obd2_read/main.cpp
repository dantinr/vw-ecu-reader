#include <Arduino.h>
#include <LittleFS.h>
#include <WebServer.h>
#include <WiFi.h>
#include <cstdio>
#include <cstring>
#include <unistd.h>
#include "driver/gpio.h"
#include "driver/twai.h"
#include "esp_partition.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "dashboard.h"
#include "dsg_response.h"
#include "pin_config.h"
#include "vw_firmware_version.h"

#if defined(OBD2_CAPTURE_TWAI_ERRORS)
#include "hal/twai_hal.h"

static volatile uint32_t lastCanErrorCode = 0;

extern "C" uint32_t __real_twai_hal_get_events(twai_hal_context_t *hal);

extern "C" uint32_t IRAM_ATTR __wrap_twai_hal_get_events(twai_hal_context_t *hal)
{
    // The SDK consumes the error capture register while handling a bus error.
    const uint32_t errorCode = hal->dev->error_code_capture_reg.val & 0xFFu;
    const uint32_t events = __real_twai_hal_get_events(hal);
    if (events & TWAI_HAL_EVENT_BUS_ERR)
        lastCanErrorCode = errorCode;
    return events;
}
#endif

#if __has_include("wifi.local.h")
#include "wifi.local.h"
#endif
#ifndef OBD2_AP_PASSWORD
#define OBD2_AP_PASSWORD ""
#endif

#if !defined(T_2Can) || defined(T_2Can_Fd)
#error "obd2_read requires T-2CAN v1.0"
#endif

namespace {
constexpr uint32_t kRequestId = 0x7DF;
constexpr uint32_t kEngineReplyId = 0x7E8;
// VW Passat B8 0GC diagnostic addressing, to be confirmed on the target car.
constexpr uint32_t kDsgRequestId = 0x7E1;
constexpr uint32_t kDsgReplyId = 0x7E9;
constexpr uint16_t kDsgIdentityDids[] = {0xF187, 0xF189, 0xF19E};
constexpr uint32_t kDsgResponseTimeoutMs = 1500;
constexpr uint8_t kRpm = 0x0C;
constexpr uint8_t kCoolant = 0x05;
constexpr uint8_t kSupportedPids = 0x00;
constexpr uint32_t kResponseTimeoutMs = 300;
constexpr uint32_t kCooldownMs = 2000;
constexpr uint32_t kCanFailureAlerts = TWAI_ALERT_BUS_OFF | TWAI_ALERT_TX_FAILED;
constexpr uint32_t kCanAlerts = kCanFailureAlerts | TWAI_ALERT_TX_SUCCESS |
                                TWAI_ALERT_ARB_LOST | TWAI_ALERT_BUS_ERROR;
constexpr auto kCanTx = static_cast<gpio_num_t>(CAN_TX);
constexpr auto kCanRx = static_cast<gpio_num_t>(CAN_RX);
constexpr char kApSsid[] = "B85380";
constexpr char kApPassword[] = OBD2_AP_PASSWORD;
static_assert(sizeof(kApPassword) == 1 || (sizeof(kApPassword) >= 9 && sizeof(kApPassword) <= 64),
              "Use an 8-63 character Wi-Fi passphrase");
constexpr char kLogPartition[] = "spiffs";
constexpr char kLogMount[] = "/littlefs";
constexpr char kLegacyLogPath[] = "/obd2.csv";
constexpr char kLogPath[] = "/obd2-v2.csv";
constexpr char kLogFile[] = "/littlefs/obd2-v2.csv";
constexpr char kLogHeader[] = "boot_id,uptime_ms,rpm,coolant_c,speed_kmh,load_pct,throttle_pct,intake_c,status\r\n";
constexpr size_t kLogHeaderSize = sizeof(kLogHeader) - 1;
constexpr size_t kLogLimit = 1024 * 1024;

enum class Encoding { Rpm, Temperature, Integer, Percent };
struct Metric
{
    const char *key;
    uint8_t pid;
    uint8_t decimals;
    Encoding encoding;
};
constexpr Metric kMetrics[] = {
    {"rpm", kRpm, 2, Encoding::Rpm},
    {"coolant", kCoolant, 0, Encoding::Temperature},
    {"speed", 0x0D, 0, Encoding::Integer},
    {"load", 0x04, 1, Encoding::Percent},
    {"throttle", 0x11, 1, Encoding::Percent},
    {"intake", 0x0F, 0, Encoding::Temperature},
};
constexpr size_t kMetricCount = sizeof(kMetrics) / sizeof(kMetrics[0]);

struct Reading
{
    bool attempted = false;
    uint32_t expected = 1;
    uint32_t received = 0;
    double values[kMetricCount] = {};
};

Reading latest;
bool hasRead = false;
uint32_t lastRead = 0;
uint32_t sampleNumber = 0;
bool checkedSupportedPids = false;
bool hasSupportedPids = false;
uint32_t supportedPids = 0;
WebServer server(80);
bool dashboardReady = false;
String dashboardHost;
enum class LogStatus { Unavailable, Empty, Ready, Full, Failed };
LogStatus logStatus = LogStatus::Unavailable;
size_t logBytes = 0;
bool hasLegacyLog = false;
char bootId[18] = {};

bool partitionIsBlank()
{
    const auto *partition = esp_partition_find_first(ESP_PARTITION_TYPE_DATA,
                                                      ESP_PARTITION_SUBTYPE_DATA_SPIFFS,
                                                      kLogPartition);
    if (!partition)
        return false;

    uint8_t block[1024];
    for (size_t offset = 0; offset < partition->size; offset += sizeof(block))
    {
        const size_t remaining = partition->size - offset;
        const size_t count = remaining < sizeof(block) ? remaining : sizeof(block);
        if (esp_partition_read_raw(partition, offset, block, count) != ESP_OK)
            return false;
        for (size_t i = 0; i < count; ++i)
            if (block[i] != 0xFF)
                return false;
    }
    return true;
}

void startLog()
{
    if (!LittleFS.begin(false, kLogMount, 4, kLogPartition))
    {
        // A shared data partition may already hold a different filesystem.
        if (!partitionIsBlank() || !LittleFS.begin(true, kLogMount, 4, kLogPartition))
        {
            Serial.println("CSV storage unavailable; existing partition left intact");
            return;
        }
    }

    hasLegacyLog = LittleFS.exists(kLegacyLogPath);
    File log = LittleFS.open(kLogPath, FILE_READ);
    if (log)
    {
        logBytes = log.size();
        if (logBytes && (!log.seek(logBytes - 1) || log.read() != '\n'))
        {
            logStatus = LogStatus::Failed;
            Serial.println("CSV has an incomplete line; old records kept, logging stopped");
            return;
        }
    }
    logStatus = logBytes >= kLogLimit ? LogStatus::Full :
                logBytes > kLogHeaderSize ? LogStatus::Ready : LogStatus::Empty;
}

const char *readingState(const Reading &reading)
{
    return !reading.attempted ? "idle" : !reading.received ? "no_data" :
           reading.received == reading.expected ? "ok" : "partial";
}

void formatMetric(char *value, size_t size, const Reading &reading, size_t index, const char *missing)
{
    if (reading.received & (1u << index))
        snprintf(value, size, "%.*f", static_cast<int>(kMetrics[index].decimals), reading.values[index]);
    else
        snprintf(value, size, "%s", missing);
}

const char *logState()
{
    switch (logStatus)
    {
    case LogStatus::Empty: return "empty";
    case LogStatus::Ready: return "ready";
    case LogStatus::Full: return "full";
    case LogStatus::Failed: return "failed";
    default: return "unavailable";
    }
}

void logReading(const Reading &reading)
{
    if (logStatus != LogStatus::Empty && logStatus != LogStatus::Ready)
        return;

    char values[kMetricCount][16];
    for (size_t i = 0; i < kMetricCount; ++i)
        formatMetric(values[i], sizeof(values[i]), reading, i, "");
    char row[160];
    const auto uptime = static_cast<unsigned long long>(esp_timer_get_time() / 1000);
    const int length = snprintf(row, sizeof(row), "%s,%llu,%s,%s,%s,%s,%s,%s,%s\r\n", bootId, uptime,
                                values[0], values[1], values[2], values[3], values[4], values[5],
                                readingState(reading));
    if (length < 0 || static_cast<size_t>(length) >= sizeof(row))
    {
        logStatus = LogStatus::Failed;
        return;
    }

    const size_t headerSize = logBytes ? 0 : kLogHeaderSize;
    const size_t newSize = logBytes + headerSize + length;
    if (newSize > kLogLimit)
    {
        logStatus = LogStatus::Full;
        Serial.println("CSV limit reached; existing records kept");
        return;
    }

    FILE *log = fopen(kLogFile, "ab");
    bool saved = false;
    if (log)
    {
        saved = (!headerSize || fwrite(kLogHeader, 1, headerSize, log) == headerSize) &&
                fwrite(row, 1, length, log) == static_cast<size_t>(length) &&
                fflush(log) == 0 && fsync(fileno(log)) == 0;
        saved = fclose(log) == 0 && saved;
    }
    if (saved)
    {
        logBytes = newSize;
        logStatus = LogStatus::Ready;
        Serial.println("CSV saved");
    }
    else
    {
        logStatus = LogStatus::Failed;
        Serial.println("CSV write failed; logging stopped");
    }
}

void sendLog(const char *path, bool available)
{
    server.sendHeader("Cache-Control", "no-store");
    if (available)
    {
        File log = LittleFS.open(path, FILE_READ);
        if (log)
        {
            server.sendHeader("Content-Disposition", String("attachment; filename=\"") + (path + 1) + '"');
            server.streamFile(log, "text/csv; charset=utf-8");
            return;
        }
    }
    server.send(404, "text/plain", "No CSV records available");
}

void sendReading()
{
    char metadata[176];
    snprintf(metadata, sizeof(metadata),
             "{\"state\":\"%s\",\"boot\":\"%s\",\"sample\":%lu,\"log\":\"%s\",\"has_log\":%s,\"has_legacy\":%s",
             readingState(latest), bootId, static_cast<unsigned long>(sampleNumber), logState(),
             logBytes > kLogHeaderSize ? "true" : "false", hasLegacyLog ? "true" : "false");
    String response;
    response.reserve(320);
    response += metadata;
    for (size_t i = 0; i < kMetricCount; ++i)
    {
        char value[16];
        formatMetric(value, sizeof(value), latest, i, "null");
        response += ",\"";
        response += kMetrics[i].key;
        response += "\":";
        response += value;
    }
    response += '}';
    server.sendHeader("Cache-Control", "no-store");
    server.send(200, "application/json", response);
}

void handleReadRequest();

bool startDashboard()
{
    if (kApPassword[0] == '\0')
    {
        Serial.println("Wi-Fi AP disabled: set OBD2_AP_PASSWORD in wifi.local.h; USB is available");
        return false;
    }
    WiFi.persistent(false);
    if (!WiFi.mode(WIFI_AP) || !WiFi.softAP(kApSsid, kApPassword, 1, false, 4))
    {
        Serial.println("Wi-Fi AP failed; USB commands remain available");
        return false;
    }

    dashboardHost = WiFi.softAPIP().toString();
    const char *headers[] = {"Origin", "X-OBD-Read"};
    server.collectHeaders(headers, sizeof(headers) / sizeof(headers[0]));
    server.on("/", HTTP_GET, [] {
        server.sendHeader("Cache-Control", "no-store");
        server.send_P(200, "text/html; charset=utf-8", kDashboard);
    });
    server.on("/api/data", HTTP_GET, sendReading);
    server.on("/api/read", HTTP_POST, handleReadRequest);
    server.on(kLogPath, HTTP_GET, [] { sendLog(kLogPath, logBytes > kLogHeaderSize); });
    server.on(kLegacyLogPath, HTTP_GET, [] { sendLog(kLegacyLogPath, hasLegacyLog); });
    server.begin();
    Serial.printf("Wi-Fi AP: %s | http://%s/\n", kApSsid, dashboardHost.c_str());
    return true;
}

constexpr bool validPayload(const uint8_t *data, uint8_t dlc, uint8_t pid, uint8_t length)
{
    return dlc >= length + 1 && dlc <= 8 && data[0] == length &&
           data[1] == 0x41 && data[2] == pid;
}

constexpr double rpm(const uint8_t *data)
{
    return (data[3] * 256u + data[4]) / 4.0;
}

constexpr int coolantC(const uint8_t *data)
{
    return static_cast<int>(data[3]) - 40;
}

constexpr double percent(const uint8_t *data)
{
    return data[3] * 100.0 / 255.0;
}

constexpr uint32_t pidMask(const uint8_t *data)
{
    return (uint32_t(data[3]) << 24) | (uint32_t(data[4]) << 16) |
           (uint32_t(data[5]) << 8) | uint32_t(data[6]);
}

constexpr bool supportsPid(uint32_t mask, uint8_t pid)
{
    return pid > 0 && pid <= 32 && (mask & (uint32_t(1) << (32 - pid)));
}

double decode(const Metric &metric, const uint8_t *data)
{
    switch (metric.encoding)
    {
    case Encoding::Rpm: return rpm(data);
    case Encoding::Temperature: return coolantC(data);
    case Encoding::Percent: return percent(data);
    default: return data[3];
    }
}

constexpr uint8_t kTestRpm[] = {4, 0x41, kRpm, 0x0C, 0x81};
constexpr uint8_t kTestCoolant[] = {3, 0x41, kCoolant, 0};
constexpr uint8_t kTestMultiFrame[] = {0x14, 0x41, kRpm, 0x0C, 0x81};
constexpr uint8_t kTestNegative[] = {3, 0x7F, 0x01, 0x12};
constexpr uint8_t kTestSupport[] = {6, 0x41, kSupportedPids, 0x18, 0x1A, 0x80, 0};
constexpr uint8_t kTestPercent[] = {3, 0x41, 0x04, 255};
static_assert(validPayload(kTestRpm, sizeof(kTestRpm), kRpm, 4) &&
              rpm(kTestRpm) == 800.25, "RPM decoding");
static_assert(validPayload(kTestCoolant, sizeof(kTestCoolant), kCoolant, 3) &&
              coolantC(kTestCoolant) == -40, "coolant decoding");
static_assert(percent(kTestPercent) == 100.0 && supportsPid(pidMask(kTestSupport), 0x0D) &&
              supportsPid(pidMask(kTestSupport), 0x11) && !supportsPid(pidMask(kTestSupport), 0x10),
              "percentage and supported PID bitmap decoding");
static_assert(!validPayload(kTestRpm, 4, kRpm, 4) &&
              !validPayload(kTestRpm, sizeof(kTestRpm), kCoolant, 4) &&
              !validPayload(kTestMultiFrame, sizeof(kTestMultiFrame), kRpm, 4) &&
              !validPayload(kTestNegative, sizeof(kTestNegative), kCoolant, 3),
              "reject malformed or unrelated frames");

void logCanDiagnostics(uint32_t alerts)
{
#if defined(OBD2_CAPTURE_TWAI_ERRORS)
    if (alerts & TWAI_ALERT_BUS_ERROR)
        Serial.printf("CAN B: ecc=0x%02X\n", static_cast<unsigned>(lastCanErrorCode));
#endif

    Serial.printf("CAN B: tx_ok=%u arbitration_lost=%u bus_error=%u bus_off=%u\n",
                  static_cast<unsigned>((alerts & TWAI_ALERT_TX_SUCCESS) != 0),
                  static_cast<unsigned>((alerts & TWAI_ALERT_ARB_LOST) != 0),
                  static_cast<unsigned>((alerts & TWAI_ALERT_BUS_ERROR) != 0),
                  static_cast<unsigned>((alerts & TWAI_ALERT_BUS_OFF) != 0));

    twai_status_info_t status = {};
    if (twai_get_status_info(&status) == ESP_OK)
    {
        Serial.printf("CAN B counters: tx_error=%lu rx_error=%lu tx_failed=%lu arbitration_lost=%lu bus_errors=%lu\n",
                      static_cast<unsigned long>(status.tx_error_counter),
                      static_cast<unsigned long>(status.rx_error_counter),
                      static_cast<unsigned long>(status.tx_failed_count),
                      static_cast<unsigned long>(status.arb_lost_count),
                      static_cast<unsigned long>(status.bus_error_count));
    }
}

bool query(uint8_t pid, uint8_t responseLength, twai_message_t &reply)
{
    twai_clear_receive_queue();
    uint32_t alerts = 0;
    // A successful query may have left its transmit-complete alert pending.
    twai_read_alerts(&alerts, 0);
    twai_message_t request = {};
    request.identifier = kRequestId;
    request.data_length_code = 8;
    request.ss = 1;
    request.data[0] = 2;
    request.data[1] = 0x01;
    request.data[2] = pid;
    const esp_err_t tx = twai_transmit(&request, 0);
    if (tx != ESP_OK)
    {
        Serial.printf("PID %02X: cannot queue request (%s)\n", pid, esp_err_to_name(tx));
        logCanDiagnostics(alerts & TWAI_ALERT_BUS_OFF);
        return false;
    }

    const uint32_t start = millis();
    uint32_t queryAlerts = 0;
    while (millis() - start < kResponseTimeoutMs)
    {
        alerts = 0;
        twai_read_alerts(&alerts, 0);
        queryAlerts |= alerts;
        if (queryAlerts & kCanFailureAlerts)
        {
            Serial.printf("PID %02X: CAN transmission failed; no retry\n", pid);
            logCanDiagnostics(queryAlerts);
            return false;
        }
        if (twai_receive(&reply, pdMS_TO_TICKS(10)) == ESP_OK &&
            !reply.extd && !reply.rtr && reply.identifier == kEngineReplyId &&
            validPayload(reply.data, reply.data_length_code, pid, responseLength))
        {
            return true;
        }
    }
    alerts = 0;
    twai_read_alerts(&alerts, 0);
    queryAlerts |= alerts;
    if (queryAlerts & kCanFailureAlerts)
        Serial.printf("PID %02X: CAN transmission failed; no retry\n", pid);
    else
        Serial.printf("PID %02X: no valid reply from 0x7E8; no retry\n", pid);
    logCanDiagnostics(queryAlerts);
    return false;
}

void keepCanBIdle()
{
    gpio_reset_pin(kCanTx);
    gpio_set_level(kCanTx, 1);
    gpio_set_direction(kCanTx, GPIO_MODE_OUTPUT);
}

esp_err_t installCanB(uint32_t replyId, uint32_t rxQueueLength)
{
    twai_general_config_t config = TWAI_GENERAL_CONFIG_DEFAULT(kCanTx, kCanRx, TWAI_MODE_NORMAL);
    config.tx_queue_len = 0;
    config.rx_queue_len = rxQueueLength;
    config.alerts_enabled = kCanAlerts;
    const twai_timing_config_t timing = TWAI_TIMING_CONFIG_500KBITS();
    // In single-filter mode, a standard CAN identifier occupies bits 31:21.
    const twai_filter_config_t filter = {replyId << 21, ~(0x7FFu << 21), true};
    return twai_driver_install(&config, &timing, &filter);
}

void readOnce()
{
    latest = Reading{};
    latest.attempted = true;
    if (installCanB(kEngineReplyId, 5) != ESP_OK)
    {
        Serial.println("CAN B: driver installation failed");
        return;
    }

    if (twai_start() == ESP_OK)
    {
        twai_message_t reply = {};
        if (query(kRpm, 4, reply))
        {
            latest.values[0] = rpm(reply.data);
            latest.received = 1;
            Serial.printf("Engine RPM: %.2f rpm\n", latest.values[0]);

            bool mayContinue = true;
            if (!checkedSupportedPids)
            {
                delay(100);
                hasSupportedPids = query(kSupportedPids, 6, reply);
                if (hasSupportedPids)
                    supportedPids = pidMask(reply.data);
                checkedSupportedPids = true;
                mayContinue = hasSupportedPids;
                if (!mayContinue)
                    latest.expected |= 1u << 1;
            }
            for (size_t i = 1; i < kMetricCount && mayContinue; ++i)
            {
                const auto &metric = kMetrics[i];
                const bool supported = hasSupportedPids ? supportsPid(supportedPids, metric.pid) : i == 1;
                if (!supported)
                    continue;
                latest.expected |= 1u << i;
                delay(100);
                if (!query(metric.pid, 3, reply))
                    break;
                latest.values[i] = decode(metric, reply.data);
                latest.received |= 1u << i;
                Serial.printf("%s: %.*f\n", metric.key, static_cast<int>(metric.decimals), latest.values[i]);
            }
        }
        twai_stop();
    }
    else
    {
        Serial.println("CAN B: start failed");
    }
    if (twai_driver_uninstall() != ESP_OK)
        Serial.println("CAN B: cleanup failed; reset board before another read");
    keepCanBIdle();
}

esp_err_t sendDsgFrame(uint8_t first, uint8_t second, uint8_t third, uint8_t fourth = 0)
{
    twai_message_t message = {};
    message.identifier = kDsgRequestId;
    message.data_length_code = 8;
    message.ss = 1;
    message.data[0] = first;
    message.data[1] = second;
    message.data[2] = third;
    message.data[3] = fourth;
    return twai_transmit(&message, 0);
}

const char *queryDsgIdentity(uint16_t did, DsgResponse &response)
{
    twai_clear_receive_queue();
    uint32_t alerts = 0;
    twai_read_alerts(&alerts, 0);
    if ((alerts & TWAI_ALERT_BUS_OFF) || sendDsgFrame(3, 0x22, did >> 8, did & 0xFF) != ESP_OK)
        return "CAN_ERROR";

    const uint32_t start = millis();
    while (millis() - start < kDsgResponseTimeoutMs)
    {
        twai_message_t reply = {};
        const auto rx = twai_receive(&reply, pdMS_TO_TICKS(10));
        alerts = 0;
        twai_read_alerts(&alerts, 0);
        if (alerts & kCanFailureAlerts)
        {
            logCanDiagnostics(alerts);
            return "CAN_ERROR";
        }
        if (rx != ESP_OK || reply.extd || reply.rtr || reply.identifier != kDsgReplyId)
            continue;

        switch (response.accept(did, reply.data, reply.data_length_code))
        {
        case DsgResponse::Result::Complete:
            return nullptr;
        case DsgResponse::Result::Negative:
            return "NRC";
        case DsgResponse::Result::Invalid:
            return "INVALID_FRAME";
        case DsgResponse::Result::FlowControl:
            // The bounded response fits in one ISO-TP block.
            if (sendDsgFrame(0x30, 0, 0) != ESP_OK)
                return "CAN_ERROR";
            break;
        default:
            break;
        }
    }
    return "TIMEOUT";
}

void readDsgIdentity()
{
    if (hasRead && millis() - lastRead < kCooldownMs)
    {
        Serial.println("DSG ERROR COOLDOWN");
        return;
    }
    if (installCanB(kDsgReplyId, 24) != ESP_OK)
    {
        Serial.println("DSG ERROR CAN_INSTALL");
        return;
    }

    const bool started = twai_start() == ESP_OK;
    if (started)
    {
        for (uint16_t did : kDsgIdentityDids)
        {
            DsgResponse response;
            const char *error = queryDsgIdentity(did, response);
            if (!error)
            {
                char hex[(DsgResponse::capacity - 3) * 2 + 1] = {};
                constexpr char digits[] = "0123456789ABCDEF";
                for (size_t index = 3; index < response.length; ++index)
                {
                    const size_t offset = (index - 3) * 2;
                    hex[offset] = digits[response.payload[index] >> 4];
                    hex[offset + 1] = digits[response.payload[index] & 0x0F];
                }
                Serial.printf("DSG %04X OK %s\n", did, hex);
            }
            else if (std::strcmp(error, "NRC") == 0)
            {
                Serial.printf("DSG %04X NRC %02X\n", did, response.nrc);
                if (did == kDsgIdentityDids[0])
                    break;
            }
            else
            {
                Serial.printf("DSG %04X %s\n", did, error);
                break;
            }
        }
        twai_stop();
    }

    const bool removed = twai_driver_uninstall() == ESP_OK;
    keepCanBIdle();
    lastRead = millis();
    hasRead = true;
    if (!removed)
        Serial.println("DSG ERROR CAN_CLEANUP");
    else if (!started)
        Serial.println("DSG ERROR CAN_START");
    else
        Serial.println("DSG END");
}

bool triggerRead()
{
    if (hasRead && millis() - lastRead < kCooldownMs)
        return false;

    readOnce();
    ++sampleNumber;
    logReading(latest);
    lastRead = millis();
    hasRead = true;
    return true;
}

void handleReadRequest()
{
    // Reject requests from links, cross-site forms, or a different host.
    if (server.hostHeader() != dashboardHost || server.header("X-OBD-Read") != "1" ||
        (server.hasHeader("Origin") && server.header("Origin") != String("http://") + dashboardHost))
    {
        server.sendHeader("Cache-Control", "no-store");
        server.send(403, "text/plain", "Request rejected");
        return;
    }

    if (!triggerRead())
    {
        server.sendHeader("Cache-Control", "no-store");
        server.sendHeader("Retry-After", "2");
        server.send(429, "text/plain", "Wait 2 seconds before another read");
        return;
    }
    sendReading();
}

void printReaderVersion()
{
    Serial.println(VW_FIRMWARE_ID);
    Serial.println("READER CAPABILITIES dsg-id");
}
} // namespace

void setup()
{
    keepCanBIdle();
    pinMode(MCP2515_RST, OUTPUT);
    digitalWrite(MCP2515_RST, LOW);
    Serial.begin(115200);
    Serial.println("OBD-II READ ONLY | CAN B 500 kbit/s | idle | type read + Enter");
    printReaderVersion();
    dashboardReady = startDashboard();
    const auto bootHigh = static_cast<unsigned>(esp_random());
    const auto bootLow = static_cast<unsigned>(esp_random());
    snprintf(bootId, sizeof(bootId), "B%08X%08X", bootHigh, bootLow);
    startLog();
}

void loop()
{
    static char command[sizeof("version") - 1] = {};
    static size_t length = 0;
    static bool overflow = false;

    if (dashboardReady)
        server.handleClient();
    while (Serial.available() > 0)
    {
        const int ch = Serial.read();
        if (ch == '\r' || ch == '\n')
        {
            if (!overflow && length == 4 && std::memcmp(command, "read", 4) == 0)
            {
                if (!triggerRead())
                    Serial.println("Wait 2 seconds before another read");
            }
            else if (!overflow && length == sizeof("version") - 1 &&
                     std::memcmp(command, "version", length) == 0)
            {
                printReaderVersion();
            }
            else if (!overflow && length == sizeof("dsg-id") - 1 &&
                     std::memcmp(command, "dsg-id", length) == 0)
            {
                readDsgIdentity();
            }
            else if (length || overflow)
            {
                Serial.println("Commands: 'read', 'version'");
            }
            length = 0;
            overflow = false;
        }
        else if (length < sizeof(command))
        {
            command[length++] = static_cast<char>(ch);
        }
        else
        {
            overflow = true;
        }
    }
    delay(10);
}
