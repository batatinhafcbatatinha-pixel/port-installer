// Shows frames rendered with Direct3D 12 through port_gfx_present_d3d12 for a few seconds: a shared texture whose
// colour changes every frame, with a moving bar drawn by scissored clears. PORT_GFX_* variables apply;
// PORT_GFX_TEST_W / _H give the window another size (a larger one makes the scaler enlarge).
// Prints "ok" when every frame was presented. Build: cl /EHsc port_gfx_d3d12_test.cpp port_gfx.c /I third_party
//   d3d12.lib dxgi.lib user32.lib
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include "port_gfx.h"

#define CHECK(hr, what) if (FAILED(hr)) { printf("%s: 0x%08lx\n", what, (unsigned long)(hr)); return 1; }

int main()
{
    const UINT W = 1280, H = 720;
    HWND window = CreateWindowExW(0, L"STATIC", L"port_gfx Direct3D 12 test", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40,
                                  (int)(getenv("PORT_GFX_TEST_W") ? atoi(getenv("PORT_GFX_TEST_W")) : 1296), (int)(getenv("PORT_GFX_TEST_H") ? atoi(getenv("PORT_GFX_TEST_H")) : 759), NULL, NULL, GetModuleHandleW(NULL), NULL);
    ID3D12Device *device = NULL;
    CHECK(D3D12CreateDevice(NULL, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)), "D3D12CreateDevice");
    D3D12_COMMAND_QUEUE_DESC qd = {};
    ID3D12CommandQueue *queue = NULL;
    CHECK(device->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)), "CreateCommandQueue");
    ID3D12CommandAllocator *alloc = NULL;
    CHECK(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc)), "CreateCommandAllocator");
    ID3D12GraphicsCommandList *list = NULL;
    CHECK(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, NULL, IID_PPV_ARGS(&list)), "CreateCommandList");
    list->Close();
    ID3D12Fence *fence = NULL;
    CHECK(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)), "CreateFence");
    HANDLE event = CreateEventW(NULL, FALSE, FALSE, NULL);

    D3D12_HEAP_PROPERTIES heap = {};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC rd = {};
    rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    rd.Width = W; rd.Height = H; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
    rd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    rd.SampleDesc.Count = 1;
    rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    ID3D12Resource *texture = NULL;
    CHECK(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &rd, D3D12_RESOURCE_STATE_COMMON, NULL,
                                          IID_PPV_ARGS(&texture)), "CreateCommittedResource (shared)");
    HANDLE shared = NULL;
    CHECK(device->CreateSharedHandle(texture, NULL, GENERIC_ALL, NULL, &shared), "CreateSharedHandle");
    D3D12_DESCRIPTOR_HEAP_DESC hd = {};
    hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV; hd.NumDescriptors = 1;
    ID3D12DescriptorHeap *rtvs = NULL;
    CHECK(device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&rtvs)), "CreateDescriptorHeap");
    D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvs->GetCPUDescriptorHandleForHeapStart();
    device->CreateRenderTargetView(texture, NULL, rtv);

    LUID luid = device->GetAdapterLuid();
    PortGfxConfig c;
    port_gfx_defaults(&c);
    port_gfx_config_from_env(&c);
    port_gfx_prefer_adapter_luid(&luid);
    if (!port_gfx_open(window, &c)) { printf("port_gfx_open: %s\n", port_gfx_error()); return 1; }
    printf("device %s, scaler %d\n", port_gfx_device_name(), port_gfx_scaler());

    int presented = 0, frames = 240;
    for (int frame = 0; frame < frames; ++frame) {
        MSG m;
        while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) DispatchMessageW(&m);
        alloc->Reset();
        list->Reset(alloc, NULL);
        D3D12_RESOURCE_BARRIER b = {};
        b.Transition.pResource = texture;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        list->ResourceBarrier(1, &b);
        float t = frame / (float)frames;
        const float back[4] = {0.1f, 0.2f + 0.5f * t, 0.6f - 0.4f * t, 1.0f};
        const float bar[4] = {1.0f, 0.9f, 0.1f, 1.0f};
        list->ClearRenderTargetView(rtv, back, 0, NULL);
        D3D12_RECT r = {(LONG)(t * (W - 80)), 200, (LONG)(t * (W - 80)) + 80, 520};
        list->ClearRenderTargetView(rtv, bar, 1, &r);
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
        list->ResourceBarrier(1, &b);
        list->Close();
        ID3D12CommandList *lists[] = {list};
        queue->ExecuteCommandLists(1, lists);
        queue->Signal(fence, frame + 1);
        fence->SetEventOnCompletion(frame + 1, event);
        WaitForSingleObject(event, INFINITE);
        int r2 = port_gfx_present_d3d12(shared, (uint32_t)rd.Format, W, H);
        if (r2 || !*port_gfx_error()) ++presented; else if (frame < 3) printf("frame %d: %s\n", frame, port_gfx_error());
        if (!r2) Sleep(16);
    }
    printf("presented %d of %d\n", presented, frames);
    port_gfx_close();
    printf(presented == frames ? "ok\n" : "FAILED\n");
    return presented == frames ? 0 : 1;
}
