#include "can_capture.h"
#include <Arduino.h>
#include <cinttypes>
#include <cstdio>
#include <cstring>
#include "driver/gpio.h"
#include "esp_timer.h"
#include "mbedtls/base64.h"
#include "pin_config.h"

namespace {
constexpr auto kTxPin = static_cast<gpio_num_t>(CAN_TX);
constexpr auto kRxPin = static_cast<gpio_num_t>(CAN_RX);
constexpr uint32_t kKeepaliveMs = 5000;
constexpr uint32_t kDrainTimeoutMs = 2000;

bool writeLine(const char *line, size_t length)
{
    return Serial.availableForWrite() >= static_cast<int>(length) &&
           Serial.write(line, length) == length;
}

void writeError(uint32_t token, const char *reason)
{
    char line[64];
    const int length = snprintf(line, sizeof(line), "\nCAN1 ERROR %08" PRIX32 " %s\n", token, reason);
    writeLine(line, length);
}

uint32_t parseToken(const char *text)
{
    uint32_t value = 0;
    for (size_t i = 0; i < 8; ++i)
    {
        const char digit = text[i];
        if (digit >= '0' && digit <= '9')
            value = (value << 4) | (digit - '0');
        else if (digit >= 'A' && digit <= 'F')
            value = (value << 4) | (digit - 'A' + 10);
        else
            return 0;
    }
    return value;
}
} // namespace

void keepCanBIdle()
{
    gpio_reset_pin(kTxPin);
    gpio_set_level(kTxPin, 1);
    gpio_set_direction(kTxPin, GPIO_MODE_OUTPUT);
}

bool CanCapture::handleCommand(const char *command, size_t length)
{
    if (length != 16 || command[7] != ' ')
        return false;
    const uint32_t request = parseToken(command + 8);
    if (!request)
        return false;

    if (std::memcmp(command, "canopen", 7) == 0)
        start(request);
    else if (std::memcmp(command, "canping", 7) == 0)
    {
        if (request == token)
            lastKeepalive = millis();
    }
    else if (std::memcmp(command, "canstop", 7) == 0)
    {
        if (request == token && !stopReason)
            beginStop("STOP");
    }
    else
        return false;
    return true;
}

void CanCapture::start(uint32_t request)
{
    if (active())
    {
        writeError(request, "BUSY");
        return;
    }
    if (Serial.availableForWrite() < static_cast<int>(kLineSize))
    {
        writeError(request, "USB_BUFFER");
        return;
    }

    twai_general_config_t config = TWAI_GENERAL_CONFIG_DEFAULT(kTxPin, kRxPin, TWAI_MODE_LISTEN_ONLY);
    config.tx_queue_len = 0;
    config.rx_queue_len = 512;
    const twai_timing_config_t timing = TWAI_TIMING_CONFIG_500KBITS();
    const twai_filter_config_t filter = TWAI_FILTER_CONFIG_ACCEPT_ALL();
    if (twai_driver_install(&config, &timing, &filter) != ESP_OK)
    {
        keepCanBIdle();
        writeError(request, "CAN_INSTALL");
        return;
    }
    if (twai_start() != ESP_OK)
    {
        twai_driver_uninstall();
        keepCanBIdle();
        writeError(request, "CAN_START");
        return;
    }

    token = request;
    lastKeepalive = lastStats = millis();
    frames = batchCount = 0;
    stopReason = nullptr;
    char line[40];
    const int length = snprintf(line, sizeof(line), "\nCAN1 START %08" PRIX32 "\n", token);
    if (!writeLine(line, length))
        beginStop("USB_BUFFER");
}

void CanCapture::beginStop(const char *reason)
{
    stopReason = twai_stop() == ESP_OK ? reason : "CAN_STOP";
    stoppingAt = millis();
}

void CanCapture::append(const twai_message_t &frame, uint32_t now)
{
    if (batchCount == 0)
        firstFrameAt = now;
    uint8_t *record = batch + batchCount * kRecordSize;
    // CAN1 record: little-endian board time (us), ID and flags, DLC, padded payload.
    const uint64_t timestamp = esp_timer_get_time();
    for (size_t i = 0; i < 8; ++i)
        record[i] = timestamp >> (i * 8);
    const uint32_t id = frame.identifier | (frame.extd ? 0x80000000u : 0) | (frame.rtr ? 0x40000000u : 0);
    for (size_t i = 0; i < 4; ++i)
        record[8 + i] = id >> (i * 8);
    record[12] = frame.data_length_code;
    const size_t dataLength = frame.rtr ? 0 : std::min<size_t>(frame.data_length_code, 8);
    std::memcpy(record + 13, frame.data, dataLength);
    std::memset(record + 13 + dataLength, 0, 8 - dataLength);
    ++batchCount;
    ++frames;
}

bool CanCapture::sendBatch()
{
    constexpr size_t kPrefixMax = 44;
    const size_t dataLength = batchCount * kRecordSize;
    if (Serial.availableForWrite() < static_cast<int>(kPrefixMax + 4 * ((dataLength + 2) / 3)))
        return false;
    char line[kLineSize];
    const int prefix = snprintf(line, sizeof(line), "\nCAN1 DATA %08" PRIX32 " %" PRIu64 " ",
                                token, frames - batchCount + 1);
    size_t encoded = 0;
    if (mbedtls_base64_encode(reinterpret_cast<unsigned char *>(line + prefix), sizeof(line) - prefix,
                             &encoded, batch, dataLength) != 0)
        return false;
    line[prefix + encoded] = '\n';
    if (!writeLine(line, prefix + encoded + 1))
        return false;
    batchCount = 0;
    return true;
}

void CanCapture::finish(bool timedOut)
{
    twai_status_info_t status = {};
    const bool gotStatus = twai_get_status_info(&status) == ESP_OK;
    const bool removed = twai_driver_uninstall() == ESP_OK;
    keepCanBIdle();
    if (!gotStatus)
        writeError(token, "CAN_STATUS");
    else
    {
        const char *reason = !removed ? "CAN_CLEANUP" : timedOut ? "DRAIN_TIMEOUT" : stopReason;
        char line[128];
        const int length = snprintf(line, sizeof(line),
            "\nCAN1 END %08" PRIX32 " %" PRIu64 " %" PRIu32 " %" PRIu32 " %" PRIu32 " %s\n",
            token, frames + status.msgs_to_rx, status.rx_missed_count, status.rx_overrun_count,
            status.bus_error_count, reason);
        writeLine(line, length);
    }
    token = 0;
}

void CanCapture::service()
{
    if (!active())
        return;
    const uint32_t now = millis();
    if (!stopReason && now - lastKeepalive >= kKeepaliveMs)
        beginStop("HOST_TIMEOUT");

    bool queueEmpty = false;
    for (int i = 0; i < 4; ++i)
    {
        twai_message_t frame;
        while (batchCount < kBatchSize)
        {
            if (twai_receive(&frame, 0) != ESP_OK)
            {
                queueEmpty = true;
                break;
            }
            append(frame, now);
        }
        if (!batchCount || (!stopReason && batchCount < kBatchSize && now - firstFrameAt < 12))
            break;
        if (!sendBatch() || queueEmpty)
            break;
    }

    if (stopReason)
    {
        if (queueEmpty && !batchCount && Serial.availableForWrite() >= 128)
            finish(false);
        else if (millis() - stoppingAt >= kDrainTimeoutMs)
            finish(true);
    }
    else if (now - lastStats >= 1000)
    {
        twai_status_info_t status;
        if (twai_get_status_info(&status) != ESP_OK)
        {
            beginStop("CAN_STATUS");
            return;
        }
        char line[80];
        const int length = snprintf(line, sizeof(line),
            "\nCAN1 STATS %08" PRIX32 " %" PRIu32 " %" PRIu32 " %" PRIu32 "\n",
            token, status.rx_missed_count, status.rx_overrun_count, status.bus_error_count);
        if (writeLine(line, length))
            lastStats = now;
    }
}
