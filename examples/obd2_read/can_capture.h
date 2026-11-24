#pragma once

#include <cstddef>
#include <cstdint>
#include "driver/twai.h"

void keepCanBIdle();

class CanCapture
{
public:
    bool active() const { return token != 0; }
    bool handleCommand(const char *command, size_t length);
    void service();

private:
    static constexpr size_t kBatchSize = 48;
    static constexpr size_t kRecordSize = 21;
    static constexpr size_t kLineSize = 4 * ((kBatchSize * kRecordSize + 2) / 3) + 64;

    void start(uint32_t request);
    void beginStop(const char *reason);
    void finish(bool timedOut);
    void append(const twai_message_t &frame, uint32_t now);
    bool sendBatch();

    uint32_t token = 0;
    uint32_t lastKeepalive = 0;
    uint32_t lastStats = 0;
    uint32_t firstFrameAt = 0;
    uint32_t stoppingAt = 0;
    const char *stopReason = nullptr;
    uint64_t frames = 0;
    size_t batchCount = 0;
    uint8_t batch[kBatchSize * kRecordSize] = {};
};
