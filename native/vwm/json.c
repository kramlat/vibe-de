#include "vwm.h"

#include <stdio.h>
#include <string.h>

/* Writes the JSON string body of `in` (no quotes) to `out`, always NUL-terminated. Returns the length. */
size_t vwm_json_escape(char *out, size_t cap, const char *in)
{
	size_t n = 0;
	if (cap == 0)
		return 0;
	for (const unsigned char *p = (const unsigned char *)(in ? in : ""); *p; p++) {
		char tmp[8];
		size_t len;
		if (*p == '"' || *p == '\\') {
			tmp[0] = '\\'; tmp[1] = (char)*p; len = 2;
		} else if (*p < 0x20) {
			len = (size_t)snprintf(tmp, sizeof tmp, "\\u%04x", *p);
		} else {
			tmp[0] = (char)*p; len = 1;
		}
		if (n + len + 1 > cap)
			break;
		memcpy(out + n, tmp, len);
		n += len;
	}
	out[n] = '\0';
	return n;
}
