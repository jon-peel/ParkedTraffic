using System.Runtime.InteropServices;

namespace ParkedTraffic;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InitPosition
{
    public double Latitude;   // degrees
    public double Longitude;  // degrees
    public double Altitude;   // feet
    public double Pitch;
    public double Bank;
    public double Heading;    // degrees true
    public uint OnGround;
    public uint Airspeed;
}

/// <summary>Raw P/Invoke surface of the native SimConnect client (flat C API).
/// Only calls that MSFS2020 supports are declared here.</summary>
internal static class Native
{
    private const string Dll = "SimConnect.dll";

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_Open(out IntPtr handle, string name, IntPtr hwnd, uint userEventWin32, IntPtr eventHandle, uint configIndex);

    [DllImport(Dll)]
    public static extern int SimConnect_Close(IntPtr handle);

    [DllImport(Dll)]
    public static extern int SimConnect_GetNextDispatch(IntPtr handle, out IntPtr data, out uint size);

    [DllImport(Dll)]
    public static extern int SimConnect_GetLastSentPacketID(IntPtr handle, out uint sendId);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_AddToFacilityDefinition(IntPtr handle, uint defineId, string fieldName);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_RequestFacilityData(IntPtr handle, uint defineId, uint requestId, string icao, string region);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_AICreateSimulatedObject(IntPtr handle, string containerTitle, InitPosition initPos, uint requestId);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_AICreateNonATCAircraft(IntPtr handle, string containerTitle, string tailNumber, InitPosition initPos, uint requestId);

    [DllImport(Dll)]
    public static extern int SimConnect_AIRemoveObject(IntPtr handle, uint objectId, uint requestId);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_AddToDataDefinition(IntPtr handle, uint defineId, string datumName, string? unitsName, uint datumType, float epsilon, uint datumId);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int SimConnect_MapClientEventToSimEvent(IntPtr handle, uint eventId, string eventName);

    /// <summary>flags: 0x10 = SIMCONNECT_EVENT_FLAG_GROUPID_IS_PRIORITY (groupId is then a priority, 1 = highest).</summary>
    [DllImport(Dll)]
    public static extern int SimConnect_TransmitClientEvent(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId, uint flags);

    [DllImport(Dll)]
    public static extern int SimConnect_SetDataOnSimObject(IntPtr handle, uint defineId, uint objectId, uint flags, uint arrayCount, uint unitSize, IntPtr data);

    /// <summary>type: 0 USER, 1 ALL, 2 AIRCRAFT, 3 HELICOPTER, 4 BOAT, 5 GROUND.</summary>
    [DllImport(Dll)]
    public static extern int SimConnect_RequestDataOnSimObjectType(IntPtr handle, uint requestId, uint defineId, uint radiusMeters, uint type);

    [DllImport(Dll)]
    public static extern int SimConnect_RequestDataOnSimObject(IntPtr handle, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);
}

/// <summary>SIMCONNECT_RECV_ID values we handle.</summary>
internal enum RecvId : uint
{
    Null = 0,
    Exception = 1,
    Open = 2,
    Quit = 3,
    SimObjectData = 8,
    SimObjectDataByType = 9,
    AssignedObjectId = 12,
    FacilityData = 28,
    FacilityDataEnd = 29,
}
