/*
 * vde-osk: VDE's on-screen keyboard.
 *
 * A Windows 11 style touch keyboard that works on any Wayland compositor, not only vwm:
 *   - input-method-v2 (+ virtual-keyboard-v1 for keys that are not text) on wlroots compositors: vwm, Sway, Hyprland...
 *   - input-method-v1 on KWin (Plasma). Registered with data/applications/vde-osk.desktop (X-KDE-Wayland-VirtualKeyboard).
 * It draws itself (cairo + pango) on a wlr-layer-shell surface, so it needs no toolkit and also fits a login screen.
 * It shows when a text field takes focus and hides when it loses it. The panel floats and is dragged by its top
 * strip (the layer surface's margins move); with --dock it spans the bottom and pushes windows up.
 *
 * Usage: vde-osk [--size PERCENT] [--theme NAME] [--dock] [--show] [--page symbols] [--png FILE]
 *   --png FILE draws the keyboard to a PNG and exits (no compositor needed); --page symbols picks the numbers page.
 *   --show keeps it visible (for trying it without a text field); SIGUSR1 toggles that.
 *   Env VDE_OSK_SIZE / VDE_OSK_THEME are used when the options are absent.
 */
#define _GNU_SOURCE
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/timerfd.h>
#include <time.h>
#include <unistd.h>

#include <cairo/cairo.h>
#include <pango/pangocairo.h>
#include <wayland-client.h>
#include <xkbcommon/xkbcommon-keysyms.h>
#include <xkbcommon/xkbcommon.h>

#include "input-method-v1-client-protocol.h"
#include "input-method-v2-client-protocol.h"
#include "virtual-keyboard-client-protocol.h"
#include "wlr-layer-shell-client-protocol.h"

/* ---------------------------------------------------------------- themes */

struct theme {
	const char *name;
	uint32_t title, body, key, special, pressed, text, hint, accent;   /* 0xRRGGBB */
};

static const struct theme themes[] = {
	{ "default",   0x2b2b2b, 0x202020, 0x3b3b3b, 0x303030, 0x585858, 0xffffff, 0xb0b0b0, 0xd6246e },
	{ "light",     0xf3f3f3, 0xf3f3f3, 0xffffff, 0xe6e6e6, 0xc9c9c9, 0x1a1a1a, 0x666666, 0x0067c0 },
	{ "dark",      0x2b2b2b, 0x202020, 0x3b3b3b, 0x303030, 0x585858, 0xffffff, 0xb0b0b0, 0x4cc2ff },
	{ "blackwhite",0x000000, 0x000000, 0x000000, 0x000000, 0x404040, 0xffffff, 0xffffff, 0xffffff },
	{ "colorpop",  0x2b2b2b, 0x202020, 0x3b3b3b, 0x303030, 0x585858, 0xffffff, 0xe0245e, 0xe0245e },
	{ "poppyred",  0xb8402f, 0xb8402f, 0xc9574a, 0xa9382a, 0xe0786b, 0xffffff, 0xffd8d2, 0xffffff },
	{ "iceblue",   0x8fa6b3, 0x8fa6b3, 0xa9bdc9, 0x7f96a3, 0xc8d8e1, 0x10202a, 0x2b3f4a, 0x10202a },
	{ "platinum",  0x8a8a8a, 0x8a8a8a, 0xa3a3a3, 0x7a7a7a, 0xc4c4c4, 0x111111, 0x333333, 0x111111 },
};
#define NTHEMES ((int)(sizeof themes / sizeof themes[0]))

/* ---------------------------------------------------------------- layout */

enum kind { K_CHAR, K_SHIFT, K_BKSP, K_ENTER, K_TAB, K_ESC, K_SPACE, K_CTRL, K_ALT, K_SUPER, K_PAGE, K_LEFT, K_RIGHT, K_GAP };

struct keydef {
	enum kind kind;
	float width;              /* in units */
	const char *lower;        /* text produced / label */
	const char *upper;        /* with Shift (NULL: same as lower) */
	const char *hint;         /* small label in the corner (what Shift gives, or the digit) */
};

#define C(l, u, h) { K_CHAR, 1, l, u, h }
#define S(k, w, label) { k, w, label, NULL, NULL }

static const struct keydef letters[4][14] = {
	{ S(K_ESC, 1, "Esc"), C("q", "Q", "1"), C("w", "W", "2"), C("e", "E", "3"), C("r", "R", "4"), C("t", "T", "5"), C("y", "Y", "6"),
	  C("u", "U", "7"), C("i", "I", "8"), C("o", "O", "9"), C("p", "P", "0"), S(K_BKSP, 2, "⌫"), },
	{ S(K_TAB, 1.5f, "Tab"), C("a", "A", NULL), C("s", "S", NULL), C("d", "D", NULL), C("f", "F", NULL), C("g", "G", NULL), C("h", "H", NULL),
	  C("j", "J", NULL), C("k", "K", NULL), C("l", "L", NULL), C("'", "\"", "\""), S(K_ENTER, 1.5f, "↵"), },
	{ S(K_SHIFT, 2, "⇧"), C("z", "Z", NULL), C("x", "X", NULL), C("c", "C", NULL), C("v", "V", NULL), C("b", "B", NULL), C("n", "N", NULL),
	  C("m", "M", NULL), C(",", ";", ";"), C(".", ":", ":"), C("?", "!", "!"), S(K_SHIFT, 1, "⇧"), },
	{ S(K_PAGE, 1, "&123"), S(K_CTRL, 1, "Ctrl"), S(K_SUPER, 1, "❖"), S(K_ALT, 1, "Alt"), { K_SPACE, 7, " ", NULL, NULL },
	  S(K_LEFT, 1, "←"), S(K_RIGHT, 1, "→"), },
};

static const struct keydef symbols[4][14] = {
	{ S(K_ESC, 1, "Esc"), C("1", "!", NULL), C("2", "@", NULL), C("3", "#", NULL), C("4", "$", NULL), C("5", "%", NULL), C("6", "^", NULL),
	  C("7", "&", NULL), C("8", "*", NULL), C("9", "(", NULL), C("0", ")", NULL), S(K_BKSP, 2, "⌫"), },
	{ S(K_TAB, 1.5f, "Tab"), C("@", NULL, NULL), C("#", NULL, NULL), C("$", NULL, NULL), C("%", NULL, NULL), C("&", NULL, NULL), C("-", "_", "_"),
	  C("+", NULL, NULL), C("(", NULL, NULL), C(")", NULL, NULL), C("=", NULL, NULL), S(K_ENTER, 1.5f, "↵"), },
	{ S(K_SHIFT, 2, "⇧"), C("*", NULL, NULL), C("\"", NULL, NULL), C("'", NULL, NULL), C(":", NULL, NULL), C(";", NULL, NULL), C("!", NULL, NULL),
	  C("?", NULL, NULL), C("/", "\\", "\\"), C("[", "{", "{"), C("]", "}", "}"), S(K_SHIFT, 1, "⇧"), },
	{ S(K_PAGE, 1, "abc"), S(K_CTRL, 1, "Ctrl"), S(K_SUPER, 1, "❖"), S(K_ALT, 1, "Alt"), { K_SPACE, 7, " ", NULL, NULL },
	  S(K_LEFT, 1, "←"), S(K_RIGHT, 1, "→"), },
};

struct key { const struct keydef *def; float x, y, w, h; };

/* ---------------------------------------------------------------- state */

#define MOD_SHIFT 1
#define MOD_CTRL 4
#define MOD_ALT 8
#define MOD_SUPER 64

struct osk {
	struct wl_display *display;
	struct wl_registry *registry;
	struct wl_compositor *compositor;
	struct wl_shm *shm;
	struct wl_seat *seat;
	struct wl_pointer *pointer;
	struct wl_touch *touch;
	struct wl_output *output;
	struct zwlr_layer_shell_v1 *layer_shell;
	struct zwp_input_method_manager_v2 *im2_manager;
	struct zwp_virtual_keyboard_manager_v1 *vk_manager;
	struct zwp_input_method_v1 *im1;

	/* backends */
	struct zwp_input_method_v2 *im2;
	struct zwp_virtual_keyboard_v1 *vk;
	struct zwp_input_method_context_v1 *ctx1;
	uint32_t ctx1_serial;
	uint32_t im2_serial;
	bool im2_pending_active, im2_active;
	uint32_t im2_pending_purpose;
	char *surrounding; uint32_t cursor;

	/* surface */
	struct wl_surface *surface;
	struct zwlr_layer_surface_v1 *layer;
	bool configured;
	int width, height;                 /* surface size in px */
	int out_w, out_h;                  /* first output's mode */
	int margin_left, margin_bottom;
	struct wl_buffer *buffer;
	void *data; size_t data_size;

	/* options */
	int size_percent;
	const struct theme *theme;
	bool dock, show_always, pinned_visible, wants_visible;

	/* keyboard state */
	int page;                          /* 0 letters, 1 symbols */
	int mods;                          /* sticky Shift/Ctrl/Alt/Super for the next key */
	struct key keys[64]; int nkeys;
	int pressed;                       /* key index, -1 none */
	bool dragging; double drag_x;
	double px, py;                     /* pointer in surface coordinates */
	int touch_id;
	int timer_fd;                      /* key repeat */
	int repeat_key;
	uint32_t seat_caps;
	bool running;
};

static struct osk osk;
static int signal_pipe[2];

/* ---------------------------------------------------------------- geometry */

static double unit(void) { return 62.0 * osk.size_percent / 100.0; }
static double gap(void) { return 6.0 * osk.size_percent / 100.0; }
static double title_h(void) { return 32.0 * osk.size_percent / 100.0; }
static double pad(void) { return 8.0 * osk.size_percent / 100.0; }

static void measure(int *w, int *h)
{
	*w = (int)ceil(13 * unit() + 2 * pad());
	*h = (int)ceil(title_h() + pad() + 4 * unit() + pad());
}

static void build_keys(void)
{
	const struct keydef (*rows)[14] = osk.page ? symbols : letters;
	osk.nkeys = 0;
	double u = unit(), g = gap();
	for (int r = 0; r < 4; r++) {
		double x = pad();
		double y = title_h() + pad() + r * u;
		for (int i = 0; i < 14; i++) {
			const struct keydef *d = &rows[r][i];
			if (!d->lower) break;
			struct key *k = &osk.keys[osk.nkeys++];
			k->def = d;
			k->x = x + g / 2;
			k->y = y + g / 2;
			k->w = d->width * u - g;
			k->h = u - g;
			x += d->width * u;
		}
	}
}

/* ---------------------------------------------------------------- drawing */

static void set_rgb(cairo_t *cr, uint32_t c, double a)
{
	cairo_set_source_rgba(cr, ((c >> 16) & 0xff) / 255.0, ((c >> 8) & 0xff) / 255.0, (c & 0xff) / 255.0, a);
}

static void round_rect(cairo_t *cr, double x, double y, double w, double h, double r)
{
	cairo_new_sub_path(cr);
	cairo_arc(cr, x + w - r, y + r, r, -M_PI / 2, 0);
	cairo_arc(cr, x + w - r, y + h - r, r, 0, M_PI / 2);
	cairo_arc(cr, x + r, y + h - r, r, M_PI / 2, M_PI);
	cairo_arc(cr, x + r, y + r, r, M_PI, 3 * M_PI / 2);
	cairo_close_path(cr);
}

static void text(cairo_t *cr, const char *s, double size, double x, double y, double w, double h, int align /* 0 centre, -1 left */)
{
	PangoLayout *l = pango_cairo_create_layout(cr);
	char font[32];
	snprintf(font, sizeof font, "Sans %.1f", size * 0.75);
	PangoFontDescription *fd = pango_font_description_from_string(font);
	pango_layout_set_font_description(l, fd);
	pango_layout_set_text(l, s, -1);
	int tw, th;
	pango_layout_get_pixel_size(l, &tw, &th);
	cairo_move_to(cr, align == 0 ? x + (w - tw) / 2.0 : x, y + (h - th) / 2.0);
	pango_cairo_show_layout(cr, l);
	pango_font_description_free(fd);
	g_object_unref(l);
}

static bool mod_active(enum kind k)
{
	return (k == K_SHIFT && (osk.mods & MOD_SHIFT)) || (k == K_CTRL && (osk.mods & MOD_CTRL)) ||
	       (k == K_ALT && (osk.mods & MOD_ALT)) || (k == K_SUPER && (osk.mods & MOD_SUPER));
}

static void draw(cairo_t *cr)
{
	const struct theme *t = osk.theme;
	double s = osk.size_percent / 100.0;
	/* body, with the title strip on top */
	cairo_set_operator(cr, CAIRO_OPERATOR_SOURCE);
	set_rgb(cr, t->body, 1);
	cairo_paint(cr);
	cairo_set_operator(cr, CAIRO_OPERATOR_OVER);
	set_rgb(cr, t->title, 1);
	cairo_rectangle(cr, 0, 0, osk.width, title_h());
	cairo_fill(cr);

	/* grip, settings cog, close */
	set_rgb(cr, t->text, 0.85);
	cairo_set_line_width(cr, 2 * s);
	cairo_move_to(cr, osk.width / 2.0 - 24 * s, title_h() / 2);
	cairo_line_to(cr, osk.width / 2.0 + 24 * s, title_h() / 2);
	cairo_stroke(cr);
	text(cr, "⚙", 16 * s, 8 * s, 0, 28 * s, title_h(), 0);
	text(cr, "✕", 14 * s, osk.width - 36 * s, 0, 28 * s, title_h(), 0);

	for (int i = 0; i < osk.nkeys; i++) {
		struct key *k = &osk.keys[i];
		const struct keydef *d = k->def;
		bool special = d->kind != K_CHAR && d->kind != K_SPACE;
		bool on = i == osk.pressed || mod_active(d->kind);
		set_rgb(cr, on ? (mod_active(d->kind) && i != osk.pressed ? t->accent : t->pressed) : (special ? t->special : t->key), 1);
		round_rect(cr, k->x, k->y, k->w, k->h, 5 * s);
		cairo_fill(cr);
		if (t->key == t->body) {   /* black & white: keys are outlined */
			set_rgb(cr, t->text, 1);
			cairo_set_line_width(cr, 1.5);
			round_rect(cr, k->x, k->y, k->w, k->h, 5 * s);
			cairo_stroke(cr);
		}
		bool shifted = osk.mods & MOD_SHIFT;
		const char *label = d->lower;
		if (d->kind == K_CHAR && shifted && d->upper) label = d->upper;
		if (d->kind == K_SPACE) continue;
		bool word = d->kind == K_ESC || d->kind == K_TAB || d->kind == K_CTRL || d->kind == K_ALT || d->kind == K_PAGE;
		set_rgb(cr, (mod_active(d->kind) && i != osk.pressed) ? (t->accent == t->text ? t->body : 0xffffff) : t->text, 1);
		text(cr, label, (word ? 16 : 22) * s, k->x, k->y, k->w, k->h, 0);
		if (d->kind == K_CHAR && d->hint && !(shifted && d->upper)) {
			set_rgb(cr, t->hint, 1);
			text(cr, d->hint, 12 * s, k->x + 6 * s, k->y + 2 * s, k->w, 16 * s, -1);
		}
	}
}

static int create_shm(size_t size)
{
	int fd = memfd_create("vde-osk", MFD_CLOEXEC);
	if (fd < 0 || ftruncate(fd, (off_t)size) < 0) { if (fd >= 0) close(fd); return -1; }
	return fd;
}

static void buffer_release(void *data, struct wl_buffer *b) { }
static const struct wl_buffer_listener buffer_listener = { buffer_release };

static void render(void)
{
	if (!osk.surface || !osk.configured || osk.width <= 0) return;
	int stride = osk.width * 4;
	size_t size = (size_t)stride * osk.height;
	int fd = create_shm(size);
	if (fd < 0) return;
	void *data = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
	if (data == MAP_FAILED) { close(fd); return; }
	cairo_surface_t *cs = cairo_image_surface_create_for_data(data, CAIRO_FORMAT_ARGB32, osk.width, osk.height, stride);
	cairo_t *cr = cairo_create(cs);
	draw(cr);
	cairo_destroy(cr);
	cairo_surface_destroy(cs);
	struct wl_shm_pool *pool = wl_shm_create_pool(osk.shm, fd, (int)size);
	struct wl_buffer *buf = wl_shm_pool_create_buffer(pool, 0, osk.width, osk.height, stride, WL_SHM_FORMAT_ARGB8888);
	wl_shm_pool_destroy(pool);
	munmap(data, size);
	close(fd);
	wl_buffer_add_listener(buf, &buffer_listener, NULL);
	if (osk.buffer) wl_buffer_destroy(osk.buffer);
	osk.buffer = buf;
	wl_surface_attach(osk.surface, buf, 0, 0);
	wl_surface_damage_buffer(osk.surface, 0, 0, osk.width, osk.height);
	wl_surface_commit(osk.surface);
}

/* ---------------------------------------------------------------- show / hide the surface */

static void layer_configure(void *data, struct zwlr_layer_surface_v1 *l, uint32_t serial, uint32_t w, uint32_t h)
{
	zwlr_layer_surface_v1_ack_configure(l, serial);
	if (w) osk.width = (int)w;
	if (h) osk.height = (int)h;
	osk.configured = true;
	build_keys();
	render();
}

static void layer_closed(void *data, struct zwlr_layer_surface_v1 *l);
static const struct zwlr_layer_surface_v1_listener layer_listener = { layer_configure, layer_closed };

static void apply_margins(void)
{
	if (!osk.layer || osk.dock) return;
	zwlr_layer_surface_v1_set_margin(osk.layer, 0, 0, osk.margin_bottom, osk.margin_left);
	wl_surface_commit(osk.surface);
}

static void surface_destroy(void)
{
	if (osk.layer) { zwlr_layer_surface_v1_destroy(osk.layer); osk.layer = NULL; }
	if (osk.surface) { wl_surface_destroy(osk.surface); osk.surface = NULL; }
	if (osk.buffer) { wl_buffer_destroy(osk.buffer); osk.buffer = NULL; }
	osk.configured = false;
	osk.pressed = -1;
	osk.dragging = false;
}

static void layer_closed(void *data, struct zwlr_layer_surface_v1 *l) { surface_destroy(); }

static void surface_create(void)
{
	if (osk.surface || !osk.layer_shell) return;
	int w, h;
	measure(&w, &h);
	if (osk.dock) w = 0;
	osk.surface = wl_compositor_create_surface(osk.compositor);
	osk.layer = zwlr_layer_shell_v1_get_layer_surface(osk.layer_shell, osk.surface, osk.output, ZWLR_LAYER_SHELL_V1_LAYER_TOP, "vde-osk");
	zwlr_layer_surface_v1_add_listener(osk.layer, &layer_listener, NULL);
	zwlr_layer_surface_v1_set_keyboard_interactivity(osk.layer, ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_NONE);
	zwlr_layer_surface_v1_set_size(osk.layer, w, h);
	if (osk.dock) {
		zwlr_layer_surface_v1_set_anchor(osk.layer, ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM);
		zwlr_layer_surface_v1_set_exclusive_zone(osk.layer, h);
	} else {
		int pw = w;
		if (osk.margin_left < 0) osk.margin_left = osk.out_w > pw ? (osk.out_w - pw) / 2 : 0;
		zwlr_layer_surface_v1_set_anchor(osk.layer, ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM);
		zwlr_layer_surface_v1_set_margin(osk.layer, 0, 0, osk.margin_bottom, osk.margin_left);
	}
	wl_surface_commit(osk.surface);
}

/* shown while a text field wants input, or when pinned (--show / SIGUSR1) */
static void update_visibility(void)
{
	bool want = osk.wants_visible || osk.pinned_visible;
	if (want && !osk.surface) surface_create();
	else if (!want && osk.surface) surface_destroy();
}

/* ---------------------------------------------------------------- sending input */

/* evdev keycodes for the US layout the virtual keyboard's keymap uses */
static int evdev_for(const char *s)
{
	static const char *rows[] = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
	static const int first[] = { 16, 30, 44 };
	if (!s || !s[0] || s[1]) return -1;
	char c = (char)tolower((unsigned char)s[0]);
	for (int r = 0; r < 3; r++) {
		const char *p = strchr(rows[r], c);
		if (p) return first[r] + (int)(p - rows[r]);
	}
	if (c >= '1' && c <= '9') return 2 + (c - '1');
	switch (c) { case '0': return 11; case '-': return 12; case '=': return 13; case '[': return 26; case ']': return 27; case '\\': return 43;
	             case ';': return 39; case '\'': return 40; case '`': return 41; case ',': return 51; case '.': return 52; case '/': return 53;
	             case ' ': return 57; }
	return -1;
}

/* the US-layout key (and whether Shift is down) that types an ASCII character */
static bool ascii_key(char c, int *code, bool *shift)
{
	static const char shifted_chars[] = "!@#$%^&*()_+{}|:\"<>?~";
	static const char plain_chars[]   = "1234567890-=[]\\;',./`";
	*shift = false;
	if (isupper((unsigned char)c)) {
		*shift = true;
		c = (char)tolower((unsigned char)c);
	} else if (c && strchr(shifted_chars, c)) {
		*shift = true;
		c = plain_chars[strchr(shifted_chars, c) - shifted_chars];
	}
	char one[2] = { c, 0 };
	*code = evdev_for(one);
	return *code >= 0;
}

static uint32_t now_ms(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (uint32_t)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
}

static void vk_key(int code, int mods)
{
	if (!osk.vk) return;
	zwp_virtual_keyboard_v1_modifiers(osk.vk, mods, 0, 0, 0);
	zwp_virtual_keyboard_v1_key(osk.vk, now_ms(), code, WL_KEYBOARD_KEY_STATE_PRESSED);
	zwp_virtual_keyboard_v1_key(osk.vk, now_ms(), code, WL_KEYBOARD_KEY_STATE_RELEASED);
	zwp_virtual_keyboard_v1_modifiers(osk.vk, 0, 0, 0, 0);
	wl_display_flush(osk.display);
}

static int utf8_prev_len(void)
{
	if (!osk.surrounding) return 1;
	uint32_t c = osk.cursor;
	if (c == 0 || c > strlen(osk.surrounding)) return 1;
	int n = 1;
	while (c > (uint32_t)n && ((unsigned char)osk.surrounding[c - n] & 0xC0) == 0x80) n++;
	return n;
}

/* with no input method to commit text to, characters are typed as key presses of the virtual keyboard */
static void vk_type(const char *s)
{
	for (; *s; s++) {
		int code; bool shift;
		if (ascii_key(*s, &code, &shift))
			vk_key(code, shift ? MOD_SHIFT : 0);
	}
}

static void im_commit_text(const char *s)
{
	if (!osk.im2 && !osk.ctx1) { vk_type(s); return; }
	if (osk.im2) {
		zwp_input_method_v2_commit_string(osk.im2, s);
		zwp_input_method_v2_commit(osk.im2, osk.im2_serial);
	} else if (osk.ctx1) {
		zwp_input_method_context_v1_commit_string(osk.ctx1, osk.ctx1_serial, s);
	}
	wl_display_flush(osk.display);
}

/* a key that is not text: v2 uses the virtual keyboard (or a text fallback), v1 sends a keysym */
static void im_special(enum kind kind, int mods)
{
	static const struct { enum kind k; int evdev; uint32_t sym; const char *fallback; } map[] = {
		{ K_ESC, 1, XKB_KEY_Escape, NULL }, { K_TAB, 15, XKB_KEY_Tab, "\t" }, { K_ENTER, 28, XKB_KEY_Return, "\n" },
		{ K_BKSP, 14, XKB_KEY_BackSpace, NULL }, { K_LEFT, 105, XKB_KEY_Left, NULL }, { K_RIGHT, 106, XKB_KEY_Right, NULL },
	};
	for (size_t i = 0; i < sizeof map / sizeof map[0]; i++) {
		if (map[i].k != kind) continue;
		if (osk.im2) {
			if (osk.vk) vk_key(map[i].evdev, mods);
			else if (kind == K_BKSP) {
				zwp_input_method_v2_delete_surrounding_text(osk.im2, utf8_prev_len(), 0);
				zwp_input_method_v2_commit(osk.im2, osk.im2_serial);
				wl_display_flush(osk.display);
			} else if (map[i].fallback) im_commit_text(map[i].fallback);
		} else if (osk.ctx1) {
			zwp_input_method_context_v1_keysym(osk.ctx1, osk.ctx1_serial, now_ms(), map[i].sym, WL_KEYBOARD_KEY_STATE_PRESSED, mods);
			zwp_input_method_context_v1_keysym(osk.ctx1, osk.ctx1_serial, now_ms(), map[i].sym, WL_KEYBOARD_KEY_STATE_RELEASED, mods);
			wl_display_flush(osk.display);
		}
		return;
	}
}

static void press_key(int index)
{
	const struct keydef *d = osk.keys[index].def;
	int mods = osk.mods & (MOD_CTRL | MOD_ALT | MOD_SUPER);
	switch (d->kind) {
	case K_SHIFT: osk.mods ^= MOD_SHIFT; break;
	case K_CTRL: osk.mods ^= MOD_CTRL; break;
	case K_ALT: osk.mods ^= MOD_ALT; break;
	case K_SUPER: osk.mods ^= MOD_SUPER; break;
	case K_PAGE: osk.page ^= 1; build_keys(); osk.pressed = -1; break;
	case K_CHAR:
	case K_SPACE: {
		const char *t = d->lower;
		if ((osk.mods & MOD_SHIFT) && d->upper) t = d->upper;
		if (mods) {
			int code = evdev_for(d->lower);
			if (code >= 0) vk_key(code, mods | (osk.mods & MOD_SHIFT));
		} else im_commit_text(t);
		osk.mods &= MOD_CTRL | MOD_ALT | MOD_SUPER;   /* shift is one-shot */
		osk.mods = 0;
		break;
	}
	case K_BKSP: case K_ENTER: case K_TAB: case K_ESC: case K_LEFT: case K_RIGHT:
		im_special(d->kind, mods | (osk.mods & MOD_SHIFT));
		if (d->kind != K_BKSP && d->kind != K_LEFT && d->kind != K_RIGHT) osk.mods = 0;
		break;
	default: break;
	}
}

/* ---------------------------------------------------------------- input on our surface */

static int hit_key(double x, double y)
{
	for (int i = 0; i < osk.nkeys; i++) {
		struct key *k = &osk.keys[i];
		double g = gap() / 2;
		if (x >= k->x - g && x < k->x + k->w + g && y >= k->y - g && y < k->y + k->h + g) return i;
	}
	return -1;
}

static void arm_repeat(bool on)
{
	struct itimerspec its = { 0 };
	if (on) {
		its.it_value.tv_nsec = 400 * 1000000L;
		its.it_interval.tv_nsec = 45 * 1000000L;
	}
	timerfd_settime(osk.timer_fd, 0, &its, NULL);
}

static void run_settings(void)
{
	if (fork() == 0) {
		setsid();
		execlp("vdesettings", "vdesettings", (char *)NULL);
		_exit(127);
	}
}

static void pointer_down(double x, double y)
{
	if (y < title_h()) {
		if (x > osk.width - 44 * osk.size_percent / 100.0) { osk.pinned_visible = false; osk.wants_visible = false; update_visibility(); return; }
		if (x < 44 * osk.size_percent / 100.0) { run_settings(); return; }
		osk.dragging = !osk.dock;
		osk.drag_x = x;
		osk.py = y;
		return;
	}
	int i = hit_key(x, y);
	if (i < 0) return;
	osk.pressed = i;
	press_key(i);
	enum kind k = osk.keys[i].def->kind;
	if (k == K_BKSP || k == K_LEFT || k == K_RIGHT) { osk.repeat_key = i; arm_repeat(true); }
	render();
}

static void pointer_up(void)
{
	osk.dragging = false;
	osk.pressed = -1;
	osk.repeat_key = -1;
	arm_repeat(false);
	render();
}

static void pointer_move(double x, double y)
{
	if (osk.dragging) {
		osk.margin_left += (int)(x - osk.drag_x);
		osk.margin_bottom -= (int)(y - osk.py);
		if (osk.margin_left < 0) osk.margin_left = 0;
		if (osk.margin_bottom < 0) osk.margin_bottom = 0;
		apply_margins();
	}
}

static void ptr_enter(void *d, struct wl_pointer *p, uint32_t s, struct wl_surface *surf, wl_fixed_t x, wl_fixed_t y) { osk.px = wl_fixed_to_double(x); osk.py = wl_fixed_to_double(y); }
static void ptr_leave(void *d, struct wl_pointer *p, uint32_t s, struct wl_surface *surf) { }
static void ptr_motion(void *d, struct wl_pointer *p, uint32_t t, wl_fixed_t x, wl_fixed_t y)
{
	osk.px = wl_fixed_to_double(x); osk.py = wl_fixed_to_double(y);
	pointer_move(osk.px, osk.py);
	if (osk.dragging) { osk.drag_x = osk.px; }
}
static void ptr_button(void *d, struct wl_pointer *p, uint32_t s, uint32_t t, uint32_t button, uint32_t state)
{
	if (button != 0x110) return;   /* BTN_LEFT */
	if (state == WL_POINTER_BUTTON_STATE_PRESSED) pointer_down(osk.px, osk.py);
	else pointer_up();
}
static void ptr_axis(void *d, struct wl_pointer *p, uint32_t t, uint32_t a, wl_fixed_t v) { }
static const struct wl_pointer_listener pointer_listener = { .enter = ptr_enter, .leave = ptr_leave, .motion = ptr_motion, .button = ptr_button, .axis = ptr_axis };

static void t_down(void *d, struct wl_touch *tc, uint32_t s, uint32_t t, struct wl_surface *surf, int32_t id, wl_fixed_t x, wl_fixed_t y)
{
	osk.touch_id = id; osk.px = wl_fixed_to_double(x); osk.py = wl_fixed_to_double(y);
	pointer_down(osk.px, osk.py);
}
static void t_up(void *d, struct wl_touch *tc, uint32_t s, uint32_t t, int32_t id) { if (id == osk.touch_id) pointer_up(); }
static void t_motion(void *d, struct wl_touch *tc, uint32_t t, int32_t id, wl_fixed_t x, wl_fixed_t y)
{
	if (id != osk.touch_id) return;
	osk.px = wl_fixed_to_double(x); osk.py = wl_fixed_to_double(y);
	pointer_move(osk.px, osk.py);
	if (osk.dragging) osk.drag_x = osk.px;
}
static void t_frame(void *d, struct wl_touch *tc) { }
static void t_cancel(void *d, struct wl_touch *tc) { pointer_up(); }
static const struct wl_touch_listener touch_listener = { .down = t_down, .up = t_up, .motion = t_motion, .frame = t_frame, .cancel = t_cancel };

static void seat_caps(void *d, struct wl_seat *seat, uint32_t caps)
{
	osk.seat_caps = caps;
	if ((caps & WL_SEAT_CAPABILITY_POINTER) && !osk.pointer) { osk.pointer = wl_seat_get_pointer(seat); wl_pointer_add_listener(osk.pointer, &pointer_listener, NULL); }
	if ((caps & WL_SEAT_CAPABILITY_TOUCH) && !osk.touch) { osk.touch = wl_seat_get_touch(seat); wl_touch_add_listener(osk.touch, &touch_listener, NULL); }
}
static void seat_name(void *d, struct wl_seat *seat, const char *name) { }
static const struct wl_seat_listener seat_listener = { seat_caps, seat_name };

/* ---------------------------------------------------------------- input-method-v2 */

/* text-input content purposes (same values in text-input-v1/v3 and input-method-v2) */
#define PURPOSE_DIGITS 2
#define PURPOSE_NUMBER 3
#define PURPOSE_PHONE 4


static void im2_activate(void *d, struct zwp_input_method_v2 *im) { osk.im2_pending_active = true; }
static void im2_deactivate(void *d, struct zwp_input_method_v2 *im) { osk.im2_pending_active = false; }
static void im2_surrounding(void *d, struct zwp_input_method_v2 *im, const char *t, uint32_t cursor, uint32_t anchor)
{
	free(osk.surrounding);
	osk.surrounding = strdup(t);
	osk.cursor = cursor;
}
static void im2_cause(void *d, struct zwp_input_method_v2 *im, uint32_t c) { }
static void im2_content(void *d, struct zwp_input_method_v2 *im, uint32_t hint, uint32_t purpose) { osk.im2_pending_purpose = purpose; }
static void im2_done(void *d, struct zwp_input_method_v2 *im)
{
	osk.im2_serial++;
	osk.im2_active = osk.im2_pending_active;
	osk.wants_visible = osk.im2_active;
	if (osk.im2_active) {
		/* digits, number and phone fields get the numbers page */
		uint32_t p = osk.im2_pending_purpose;
		if (p == PURPOSE_DIGITS || p == PURPOSE_NUMBER || p == PURPOSE_PHONE) {
			if (!osk.page) { osk.page = 1; build_keys(); }
		} else if (osk.page) { osk.page = 0; build_keys(); }
	}
	update_visibility();
	render();
}
static void im2_unavailable(void *d, struct zwp_input_method_v2 *im)
{
	fprintf(stderr, "vde-osk: another input method is already running on this seat\n");
	osk.running = false;
}
static const struct zwp_input_method_v2_listener im2_listener = { im2_activate, im2_deactivate, im2_surrounding, im2_cause, im2_content, im2_done, im2_unavailable };

/* ---------------------------------------------------------------- input-method-v1 (KWin) */

static void ctx_surrounding(void *d, struct zwp_input_method_context_v1 *c, const char *t, uint32_t cur, uint32_t anchor) { }
static void ctx_reset(void *d, struct zwp_input_method_context_v1 *c) { }
static void ctx_content(void *d, struct zwp_input_method_context_v1 *c, uint32_t hint, uint32_t purpose) { }
static void ctx_action(void *d, struct zwp_input_method_context_v1 *c, uint32_t button, uint32_t index) { }
static void ctx_commit_state(void *d, struct zwp_input_method_context_v1 *c, uint32_t serial) { osk.ctx1_serial = serial; }
static void ctx_language(void *d, struct zwp_input_method_context_v1 *c, const char *l) { }
static const struct zwp_input_method_context_v1_listener ctx_listener = { ctx_surrounding, ctx_reset, ctx_content, ctx_action, ctx_commit_state, ctx_language };

static void im1_activate(void *d, struct zwp_input_method_v1 *im, struct zwp_input_method_context_v1 *ctx)
{
	if (osk.ctx1) zwp_input_method_context_v1_destroy(osk.ctx1);
	osk.ctx1 = ctx;
	zwp_input_method_context_v1_add_listener(ctx, &ctx_listener, NULL);
	osk.wants_visible = true;
	update_visibility();
}
static void im1_deactivate(void *d, struct zwp_input_method_v1 *im, struct zwp_input_method_context_v1 *ctx)
{
	if (osk.ctx1 == ctx) osk.ctx1 = NULL;
	zwp_input_method_context_v1_destroy(ctx);
	osk.wants_visible = false;
	update_visibility();
}
static const struct zwp_input_method_v1_listener im1_listener = { im1_activate, im1_deactivate };

/* ---------------------------------------------------------------- virtual keyboard keymap */

static void vk_setup(void)
{
	if (!osk.vk_manager || !osk.seat) return;
	struct xkb_context *xc = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
	struct xkb_rule_names names = { .layout = "us" };
	struct xkb_keymap *km = xc ? xkb_keymap_new_from_names(xc, &names, XKB_KEYMAP_COMPILE_NO_FLAGS) : NULL;
	char *s = km ? xkb_keymap_get_as_string(km, XKB_KEYMAP_FORMAT_TEXT_V1) : NULL;
	if (s) {
		size_t size = strlen(s) + 1;
		int fd = create_shm(size);
		if (fd >= 0) {
			void *p = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
			if (p != MAP_FAILED) {
				memcpy(p, s, size);
				munmap(p, size);
				osk.vk = zwp_virtual_keyboard_manager_v1_create_virtual_keyboard(osk.vk_manager, osk.seat);
				zwp_virtual_keyboard_v1_keymap(osk.vk, WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1, fd, (uint32_t)size);
			}
			close(fd);
		}
		free(s);
	}
	if (km) xkb_keymap_unref(km);
	if (xc) xkb_context_unref(xc);
}

/* ---------------------------------------------------------------- registry */

static void out_geometry(void *d, struct wl_output *o, int32_t x, int32_t y, int32_t pw, int32_t ph, int32_t sp, const char *make, const char *model, int32_t tr) { }
static void out_mode(void *d, struct wl_output *o, uint32_t flags, int32_t w, int32_t h, int32_t refresh)
{
	if (flags & WL_OUTPUT_MODE_CURRENT) { osk.out_w = w; osk.out_h = h; }
}
static void out_done(void *d, struct wl_output *o) { }
static void out_scale(void *d, struct wl_output *o, int32_t f) { }
static const struct wl_output_listener output_listener = { .geometry = out_geometry, .mode = out_mode, .done = out_done, .scale = out_scale };

static void global(void *d, struct wl_registry *r, uint32_t name, const char *iface, uint32_t version)
{
	if (!strcmp(iface, wl_compositor_interface.name)) osk.compositor = wl_registry_bind(r, name, &wl_compositor_interface, 4);
	else if (!strcmp(iface, wl_shm_interface.name)) osk.shm = wl_registry_bind(r, name, &wl_shm_interface, 1);
	else if (!strcmp(iface, wl_seat_interface.name) && !osk.seat) {
		osk.seat = wl_registry_bind(r, name, &wl_seat_interface, 3);
		wl_seat_add_listener(osk.seat, &seat_listener, NULL);
	} else if (!strcmp(iface, wl_output_interface.name) && !osk.output) {
		osk.output = wl_registry_bind(r, name, &wl_output_interface, 2);
		wl_output_add_listener(osk.output, &output_listener, NULL);
	} else if (!strcmp(iface, zwlr_layer_shell_v1_interface.name)) osk.layer_shell = wl_registry_bind(r, name, &zwlr_layer_shell_v1_interface, 1);
	else if (!strcmp(iface, zwp_input_method_manager_v2_interface.name)) osk.im2_manager = wl_registry_bind(r, name, &zwp_input_method_manager_v2_interface, 1);
	else if (!strcmp(iface, zwp_virtual_keyboard_manager_v1_interface.name)) osk.vk_manager = wl_registry_bind(r, name, &zwp_virtual_keyboard_manager_v1_interface, 1);
	else if (!strcmp(iface, zwp_input_method_v1_interface.name)) osk.im1 = wl_registry_bind(r, name, &zwp_input_method_v1_interface, 1);
}
static void global_remove(void *d, struct wl_registry *r, uint32_t name) { }
static const struct wl_registry_listener registry_listener = { global, global_remove };

/* ---------------------------------------------------------------- main */

static void on_signal(int sig) { char c = (char)sig; if (write(signal_pipe[1], &c, 1) < 0) { } }

static const struct theme *find_theme(const char *name)
{
	for (int i = 0; i < NTHEMES; i++)
		if (!strcasecmp(themes[i].name, name)) return &themes[i];
	return &themes[0];
}

int main(int argc, char **argv)
{
	const char *png = NULL;
	osk.size_percent = getenv("VDE_OSK_SIZE") ? atoi(getenv("VDE_OSK_SIZE")) : 100;
	osk.theme = find_theme(getenv("VDE_OSK_THEME") ? getenv("VDE_OSK_THEME") : "default");
	osk.pressed = -1; osk.repeat_key = -1; osk.margin_left = -1; osk.margin_bottom = 56;
	for (int i = 1; i < argc; i++) {
		if (!strcmp(argv[i], "--size") && i + 1 < argc) osk.size_percent = atoi(argv[++i]);
		else if (!strcmp(argv[i], "--theme") && i + 1 < argc) osk.theme = find_theme(argv[++i]);
		else if (!strcmp(argv[i], "--dock")) osk.dock = true;
		else if (!strcmp(argv[i], "--show")) osk.pinned_visible = true;
		else if (!strcmp(argv[i], "--png") && i + 1 < argc) png = argv[++i];
		else if (!strcmp(argv[i], "--page") && i + 1 < argc) osk.page = !strcmp(argv[++i], "symbols");
		else { fprintf(stderr, "usage: vde-osk [--size PERCENT] [--theme NAME] [--dock] [--show]\n"); return 2; }
	}
	if (osk.size_percent < 50) osk.size_percent = 50;
	if (osk.size_percent > 250) osk.size_percent = 250;

	if (png) {
		measure(&osk.width, &osk.height);
		build_keys();
		cairo_surface_t *cs = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, osk.width, osk.height);
		cairo_t *cr = cairo_create(cs);
		draw(cr);
		cairo_destroy(cr);
		cairo_status_t st = cairo_surface_write_to_png(cs, png);
		cairo_surface_destroy(cs);
		return st == CAIRO_STATUS_SUCCESS ? 0 : 1;
	}

	osk.display = wl_display_connect(NULL);
	if (!osk.display) { fprintf(stderr, "vde-osk: no Wayland display\n"); return 1; }
	osk.registry = wl_display_get_registry(osk.display);
	wl_registry_add_listener(osk.registry, &registry_listener, NULL);
	wl_display_roundtrip(osk.display);
	wl_display_roundtrip(osk.display);
	if (!osk.compositor || !osk.shm || !osk.seat || !osk.layer_shell) {
		fprintf(stderr, "vde-osk: the compositor lacks wl_compositor, wl_shm, wl_seat or wlr-layer-shell\n");
		return 1;
	}
	if (osk.im2_manager) {
		osk.im2 = zwp_input_method_manager_v2_get_input_method(osk.im2_manager, osk.seat);
		zwp_input_method_v2_add_listener(osk.im2, &im2_listener, NULL);
		fprintf(stderr, "vde-osk: input-method-v2%s\n", osk.vk_manager ? " + virtual-keyboard" : "");
	} else if (osk.im1) {
		zwp_input_method_v1_add_listener(osk.im1, &im1_listener, NULL);
		fprintf(stderr, "vde-osk: input-method-v1\n");
	} else if (osk.vk_manager) {
		fprintf(stderr, "vde-osk: virtual-keyboard only: shown with --show, SIGUSR1 shows or hides it; keys are typed as key presses\n");
	} else if (!osk.pinned_visible) {
		fprintf(stderr, "vde-osk: the compositor offers no input method or virtual keyboard protocol (use --show to just display the keyboard)\n");
		return 1;
	}
	vk_setup();

	if (pipe2(signal_pipe, O_NONBLOCK | O_CLOEXEC) < 0) return 1;
	struct sigaction sa = { .sa_handler = on_signal };
	sigaction(SIGUSR1, &sa, NULL);
	sigaction(SIGTERM, &sa, NULL);
	sigaction(SIGINT, &sa, NULL);
	signal(SIGCHLD, SIG_IGN);
	osk.timer_fd = timerfd_create(CLOCK_MONOTONIC, TFD_CLOEXEC | TFD_NONBLOCK);

	build_keys();
	update_visibility();
	osk.running = true;
	while (osk.running) {
		while (wl_display_prepare_read(osk.display) != 0) wl_display_dispatch_pending(osk.display);
		wl_display_flush(osk.display);
		struct pollfd fds[3] = {
			{ wl_display_get_fd(osk.display), POLLIN, 0 }, { signal_pipe[0], POLLIN, 0 }, { osk.timer_fd, POLLIN, 0 },
		};
		if (poll(fds, 3, -1) < 0 && errno != EINTR) { wl_display_cancel_read(osk.display); break; }
		if (fds[0].revents & POLLIN) { if (wl_display_read_events(osk.display) < 0) break; }
		else wl_display_cancel_read(osk.display);
		if (wl_display_dispatch_pending(osk.display) < 0) break;
		if (fds[1].revents & POLLIN) {
			char c;
			while (read(signal_pipe[0], &c, 1) > 0) {
				if (c == SIGUSR1) { osk.pinned_visible = !osk.pinned_visible; update_visibility(); }
				else osk.running = false;
			}
		}
		if (fds[2].revents & POLLIN) {
			uint64_t n;
			if (read(osk.timer_fd, &n, sizeof n) > 0 && osk.repeat_key >= 0) press_key(osk.repeat_key);
		}
	}
	surface_destroy();
	wl_display_disconnect(osk.display);
	return 0;
}
