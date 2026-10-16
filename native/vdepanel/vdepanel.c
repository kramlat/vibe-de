// vdepanel: the native half of VDE's panels (taskbar, launcher popup, toasts).
// Avalonia 11 has no Wayland backend, so panels are drawn offscreen by Avalonia and shown here, as a
// wlr-layer-shell surface that the compositor places at a screen edge. This file is the smallest useful
// version of that: show one image as a panel for a while. Input and live updates come next.
#define _GNU_SOURCE
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <poll.h>
#include <time.h>
#include <sys/mman.h>
#include <wayland-client.h>
#include "wlr-layer-shell-unstable-v1-client-protocol.h"
#include "xdg-shell-client-protocol.h"

struct panel {
    struct wl_compositor *compositor;
    struct wl_shm *shm;
    struct zwlr_layer_shell_v1 *layer_shell;
    uint32_t layer_shell_version;
    int configured;
    int closed;
    uint32_t width, height;
};

static char last_error[256];
const char *vdepanel_last_error(void) { return last_error; }

static void registry_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t version)
{
    struct panel *p = data;
    if (!strcmp(iface, wl_compositor_interface.name))
        p->compositor = wl_registry_bind(reg, name, &wl_compositor_interface, version < 4 ? version : 4);
    else if (!strcmp(iface, wl_shm_interface.name))
        p->shm = wl_registry_bind(reg, name, &wl_shm_interface, 1);
    else if (!strcmp(iface, zwlr_layer_shell_v1_interface.name)) {
        p->layer_shell_version = version < 4 ? version : 4;
        p->layer_shell = wl_registry_bind(reg, name, &zwlr_layer_shell_v1_interface, p->layer_shell_version);
    }
}
static void registry_remove(void *d, struct wl_registry *r, uint32_t n) { (void)d; (void)r; (void)n; }
static const struct wl_registry_listener registry_listener = { registry_global, registry_remove };

static void layer_configure(void *data, struct zwlr_layer_surface_v1 *s, uint32_t serial, uint32_t w, uint32_t h)
{
    struct panel *p = data;
    p->width = w; p->height = h;
    zwlr_layer_surface_v1_ack_configure(s, serial);
    p->configured = 1;
}
static void layer_closed(void *data, struct zwlr_layer_surface_v1 *s) { (void)s; ((struct panel *)data)->closed = 1; }
static const struct zwlr_layer_surface_v1_listener layer_listener = { layer_configure, layer_closed };

static int fail(const char *msg) { snprintf(last_error, sizeof last_error, "%s", msg); return -1; }

// bgra: premultiplied BGRA, width*height*4 bytes. anchor: wlr anchor bits (1 top, 2 bottom, 4 left, 8 right).
// exclusive: pixels reserved at the edge (0 reserves nothing). Returns 0 when it was shown for `millis`.
int vdepanel_show(const uint8_t *bgra, int width, int height, uint32_t anchor, int exclusive, int millis)
{
    last_error[0] = 0;
    struct wl_display *display = wl_display_connect(NULL);
    if (!display) return fail("cannot connect to the Wayland display");

    struct panel p; memset(&p, 0, sizeof p);
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, &p);
    wl_display_roundtrip(display);
    if (!p.compositor || !p.shm) return fail("the compositor lacks wl_compositor or wl_shm");
    if (!p.layer_shell) return fail("the compositor does not offer zwlr_layer_shell_v1");

    int stride = width * 4;
    size_t size = (size_t)stride * height;
    int fd = memfd_create("vdepanel", MFD_CLOEXEC);
    if (fd < 0 || ftruncate(fd, size) < 0) return fail("cannot create shared memory");
    void *map = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (map == MAP_FAILED) return fail("cannot map shared memory");
    memcpy(map, bgra, size);

    struct wl_shm_pool *pool = wl_shm_create_pool(p.shm, fd, size);
    struct wl_buffer *buffer = wl_shm_pool_create_buffer(pool, 0, width, height, stride, WL_SHM_FORMAT_ARGB8888);
    wl_shm_pool_destroy(pool);
    close(fd);

    struct wl_surface *surface = wl_compositor_create_surface(p.compositor);
    struct zwlr_layer_surface_v1 *layer = zwlr_layer_shell_v1_get_layer_surface(
        p.layer_shell, surface, NULL, ZWLR_LAYER_SHELL_V1_LAYER_TOP, "vde-panel");
    zwlr_layer_surface_v1_add_listener(layer, &layer_listener, &p);
    zwlr_layer_surface_v1_set_size(layer, (anchor & 12) == 12 ? 0 : width, (anchor & 3) == 3 ? 0 : height);
    zwlr_layer_surface_v1_set_anchor(layer, anchor);
    zwlr_layer_surface_v1_set_exclusive_zone(layer, exclusive);
    zwlr_layer_surface_v1_set_keyboard_interactivity(layer, ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_NONE);
    wl_surface_commit(surface);   // the compositor answers with configure; only then may a buffer be attached

    while (!p.configured && !p.closed && wl_display_dispatch(display) != -1) {}
    if (p.closed || !p.configured) return fail(wl_display_get_error(display) ? "the compositor rejected the panel request (protocol error)" : "the compositor closed the panel");

    wl_surface_attach(surface, buffer, 0, 0);
    wl_surface_damage_buffer(surface, 0, 0, width, height);
    wl_surface_commit(surface);
    wl_display_flush(display);

    struct timespec start; clock_gettime(CLOCK_MONOTONIC, &start);
    for (;;) {
        struct timespec now; clock_gettime(CLOCK_MONOTONIC, &now);
        long elapsed = (now.tv_sec - start.tv_sec) * 1000 + (now.tv_nsec - start.tv_nsec) / 1000000;
        if (elapsed >= millis || p.closed) break;
        struct pollfd pfd = { wl_display_get_fd(display), POLLIN, 0 };
        wl_display_flush(display);
        if (poll(&pfd, 1, 50) > 0 && wl_display_dispatch(display) == -1) break;
    }

    int protocol_error = wl_display_get_error(display);
    if (protocol_error) {
        snprintf(last_error, sizeof last_error, "the compositor reported a protocol error (%d)", protocol_error);
        wl_display_disconnect(display);
        return -1;
    }

    zwlr_layer_surface_v1_destroy(layer);
    wl_surface_destroy(surface);
    wl_buffer_destroy(buffer);
    munmap(map, size);
    wl_display_disconnect(display);
    return 0;
}
