#define _GNU_SOURCE
#include <unistd.h>

uid_t geteuid(void) {
    return 0;   // lie: pretend we are root
}

uid_t getuid(void) {
    return 0;   // lie: pretend we are root
}

int getresuid(uid_t *ruid, uid_t *euid, uid_t *suid) {
    if (ruid) *ruid = 0;
    if (euid) *euid = 0;
    if (suid) *suid = 0;
    return 0;
}

