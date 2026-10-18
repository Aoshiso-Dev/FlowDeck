// Hardware-free bus model. Actual installed TFT/ST77916 transfer and rotation
// functions are appended by render_host.py, rather than reimplemented here.
struct DriverBus {
    std::vector<uint16_t> physical = std::vector<uint16_t>(360 * 360, 0xFFFF);
    uint8_t madctl = 0;
    int left = 0, top = 0, right = 359, bottom = 359, offset = 0;
    void beginWrite() {}
    void endWrite() {}
    void writeC8D8(uint8_t command, uint8_t value) {
        if (command == ST77916_MADCTL) madctl = value;
        else { std::cerr << "unexpected register"; std::exit(30); }
    }
    void writeC8D16D16(uint8_t command, uint16_t a, uint16_t b) {
        if (command == ST77916_CASET) { left = a; right = b; }
        else if (command == ST77916_RASET) { top = a; bottom = b; }
        else { std::cerr << "unexpected window command"; std::exit(30); }
    }
    void writeCommand(uint8_t command) {
        if (command != ST77916_RAMWR) { std::cerr << "unexpected pixel command"; std::exit(30); }
        offset = 0;
    }
    void writePixels(uint16_t* data, uint32_t count) {
        const int width = right - left + 1;
        if (left < 0 || top < 0 || right > 359 || bottom > 359 || width <= 0 || bottom < top)
            { std::cerr << "invalid LCD address window"; std::exit(30); }
        for (uint32_t n = 0; n < count; ++n, ++offset) {
            int x = left + offset % width, y = top + offset / width;
            if (y > bottom || (madctl & ST77916_MADCTL_MV))
                { std::cerr << "invalid LCD stream"; std::exit(30); }
            if (madctl & ST77916_MADCTL_MX) x = 359 - x;
            if (madctl & ST77916_MADCTL_MY) y = 359 - y;
            physical[y * 360 + x] = data[n];
        }
    }
};
class Arduino_TFT : public Arduino_GFX {
public:
    DriverBus* _bus;
    uint16_t _currentX = 0xFFFF, _currentY = 0xFFFF, _currentW = 0xFFFF, _currentH = 0xFFFF;
    int _xStart = 0, _yStart = 0;
    const int COL_OFFSET1 = 0, ROW_OFFSET1 = 0, COL_OFFSET2 = 0, ROW_OFFSET2 = 0;
    Arduino_TFT(DriverBus* bus) : Arduino_GFX(360, 360), _bus(bus) {}
    bool begin(int32_t = -1) override { return true; }
    void writePixelPreclipped(int16_t, int16_t, uint16_t) override { std::exit(30); }
    void setRotation(uint8_t r) override;
    virtual void writeAddrWindow(int16_t x, int16_t y, uint16_t w, uint16_t h) = 0;
    void startWrite() override { _bus->beginWrite(); }
    void endWrite() override { _bus->endWrite(); }
    void draw16bitRGBBitmap(int16_t x, int16_t y, uint16_t* bitmap, int16_t w, int16_t h);
};
class Arduino_ST77916 : public Arduino_TFT {
public:
    explicit Arduino_ST77916(DriverBus* bus) : Arduino_TFT(bus) {}
    void setRotation(uint8_t r) override;
    void writeAddrWindow(int16_t x, int16_t y, uint16_t w, uint16_t h) override;
};
