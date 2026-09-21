using NAudio.CoreAudioApi;
using System;
using System.Runtime.InteropServices;

namespace WindowWise.Services;

// Windows가 제공하는 PolicyConfig COM 객체다.
// 이 객체를 통해 실제 Windows 기본 오디오 장치를 변경한다.
[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClient
{
}

// COM 객체의 함수 배치 순서를 C#에 알려주는 인터페이스
// SetDefaultEndpoint 앞의 함수들을 사용하지 않더라도 순서대로 선언해야함.
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat(
        //MarshalAs는 C# string 변환방법을 지정함.
        //이 경우에는 C# string -> UTF-16 문자열 데이터 -> Windows 함수가 이해할 수 있는 LPWSTR 포인터로 변환됨.
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr format);

    [PreserveSig]
    int GetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        int defaultFormat,
        IntPtr format);

    [PreserveSig]
    int ResetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr endpointFormat,
        IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        int defaultPeriod,
        IntPtr defaultPeriodValue,
        IntPtr minimumPeriodValue);

    [PreserveSig]
    int SetProcessingPeriod(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr period);

    [PreserveSig]
    int GetShareMode(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr mode);

    [PreserveSig]
    int SetShareMode(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr mode);

    [PreserveSig]
    int GetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr key,
        IntPtr value);

    [PreserveSig]
    int SetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr key,
        IntPtr value);

    [PreserveSig]
    int SetDefaultEndpoint(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        Role role);

    [PreserveSig]
    int SetEndpointVisibility(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        int visible);
}

// 나머지 코드가 COM 세부사항을 몰라도 되도록 감싸는 어댑터.
internal sealed class DefaultAudioEndpointController : IDisposable
{
    private IPolicyConfig? _policyConfig;

    public DefaultAudioEndpointController()
    {
        _policyConfig = (IPolicyConfig)new PolicyConfigClient();
    }

    public bool TrySetDefaultEndpoint(string deviceId, Role role)
    {
        if (_policyConfig is null || string.IsNullOrWhiteSpace(deviceId))
            return false;

        int result = _policyConfig.SetDefaultEndpoint(deviceId, role);

        // HRESULT는 0 이상이면 성공, 음수이면 실패.
        return result >= 0;
    }


    //COM 으로 만들어진 객체는 일반적인 C# 객체와 수명이 다른식으로 작동함
    public void Dispose()
    {
        if (_policyConfig is null)
            return;
        // COM 객체를 수동으로 사용이 끝났다고 알려주고 메모리에서 해제
        if (Marshal.IsComObject(_policyConfig))
            Marshal.FinalReleaseComObject(_policyConfig);

        _policyConfig = null;
    }
}
