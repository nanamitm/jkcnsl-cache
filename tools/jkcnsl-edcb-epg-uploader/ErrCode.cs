namespace EpgTimer;

public enum ErrCode : uint
{
    CMD_ERR = 0,
    CMD_SUCCESS = 1,
    CMD_NON_SUPPORT = 203,
    CMD_ERR_INVALID_ARG = 204,
    CMD_ERR_CONNECT = 205,
    CMD_ERR_DISCONNECT = 206,
    CMD_ERR_TIMEOUT = 207,
    CMD_ERR_BUSY = 208
}
