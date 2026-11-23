/* port_input_test [seconds] [exe_dir]: lists the controllers port_input sees and prints player 1 while it runs,
 * with a short rumble at the start. SDL3.dll comes from exe_dir (default: this program's folder). */
#include "port_input.h"

#include <stdio.h>
#include <stdlib.h>
#if defined(_WIN32)
#include <windows.h>
#endif

int main(int argc, char **argv)
{
    int seconds = argc > 1 ? atoi(argv[1]) : 5;
    const char *dir = argc > 2 ? argv[2] : NULL;
    char here[1024] = "";
#if defined(_WIN32)
    if (!dir) {
        GetModuleFileNameA(NULL, here, sizeof here);
        char *slash = strrchr(here, '\\');
        if (slash) *slash = 0;
        dir = here;
    }
#endif
    if (!port_input_start(dir, NULL)) {
        printf("SDL3 not running\n");
        return 1;
    }
    port_input_update();
    printf("%d controller(s)\n", port_input_count());
    for (int i = 0; i < port_input_count(); i++)
        printf("  %d: %s  [%s]\n", i, port_input_device_name(i), port_input_device_guid(i));
    port_input_rumble(0, 0.6f, 0.6f);
    for (int t = 0; t < seconds * 10; t++) {
        PortPad pad;
        port_input_update();
        if (t == 5) port_input_rumble(0, 0.0f, 0.0f);
        port_input_pad(0, &pad);
        if (t % 10 == 0)
            printf("p1 %s%s buttons=%05X l=(%.2f,%.2f) r=(%.2f,%.2f) lt=%.2f rt=%.2f\n", pad.connected ? "on " : "off",
                   pad.wheel ? " wheel" : "", pad.buttons, pad.lx, pad.ly, pad.rx, pad.ry, pad.lt, pad.rt);
#if defined(_WIN32)
        Sleep(100);
#endif
    }
    return 0;
}
