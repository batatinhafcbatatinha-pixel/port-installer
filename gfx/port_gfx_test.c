/* Opens a window and shows a moving test pattern through port_gfx for a few seconds (PORT_GFX_* variables apply). */
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include "port_gfx.h"
int main(void)
{
    static uint32_t px[256 * 224];
    PortGfxConfig c;
    HWND w = CreateWindowExW(0, L"STATIC", L"port_gfx test", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 100, 100, 960, 720,
                             NULL, NULL, GetModuleHandleW(NULL), NULL);
    port_gfx_defaults(&c);
    port_gfx_config_from_env(&c);
    if (!port_gfx_open(w, &c)) { printf("port_gfx_open: %s\n", port_gfx_error()); return 1; }
    printf("device %s, scaler %d\n", port_gfx_device_name(), port_gfx_scaler());
    for (int frame = 0; frame < 300; ++frame) {
        MSG m;
        while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) DispatchMessageW(&m);
        for (int y = 0; y < 224; ++y)
            for (int x = 0; x < 256; ++x)
                px[y * 256 + x] = ((x + frame) & 32) ^ (y & 32) ? 0xFFFFFFFFu : 0xFF2040A0u;
        if (!port_gfx_present_pixels(px, 256, 224, 256 * 4)) Sleep(16);
    }
    port_gfx_close();
    printf("ok\n");
    return 0;
}
