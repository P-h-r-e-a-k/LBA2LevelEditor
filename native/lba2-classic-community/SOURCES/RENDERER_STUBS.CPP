#include "DEFINES.H"
#include "PERSO.H"
#include "SAVEGAME.H"

S32 EscTimer = 0;
S32 SaveLoadTestMode = 0;

void Message(const char *message, S32 flag)
{
    (void)message;
    (void)flag;
}

void GamePaused(U32 flags)
{
    (void)flags;
}

void CheckSaveLogPcx(U8 *palette)
{
    (void)palette;
}

void InitGame(int argc)
{
    (void)argc;
}

S32 MainLoop(void)
{
    return 0;
}

extern "C" void Console_RegisterAll(void)
{
}
