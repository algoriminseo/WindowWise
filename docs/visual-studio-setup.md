# Visual Studio에서 구현 시작하기

현재 골격은 코드가 전혀 없는 상태이므로 빌드 결과는 DLL입니다. 첫 화면을 만들 때 다음 절차로 WPF 실행 프로젝트로 전환합니다.

1. Visual Studio에서 `WindowWise.slnx`를 엽니다.
2. `WindowWise.App` 프로젝트를 우클릭하고 **추가 > 새 항목**을 선택합니다.
3. **Application Definition (WPF)** 항목을 `App.xaml` 이름으로 추가합니다.
4. **Window (WPF)** 항목을 `MainWindow.xaml` 이름으로 추가합니다.
5. `WindowWise.App` 속성에서 출력 형식을 **Windows 응용 프로그램**으로 바꿉니다.
   - 또는 `WindowWise.App.csproj`의 `<OutputType>Library</OutputType>`를 `<OutputType>WinExe</OutputType>`로 변경합니다.
6. 프로젝트를 시작 프로젝트로 지정하고 실행합니다.

## Visual Studio에서 새로 만드는 편이 더 나은 경우

WPF 템플릿 사용이 처음이라면 Visual Studio가 기본 `App.xaml`과 `MainWindow.xaml`을 만들어 주는 쪽이 조금 더 안전합니다.

1. **새 프로젝트 만들기**
2. **WPF Application** 선택 (`WPF App (.NET Framework)`가 아님)
3. 프로젝트 이름 `WindowWise.App`, 솔루션 이름 `WindowWise`
4. Framework는 **.NET 10.0** 선택
5. 생성 후 이 골격의 `Views`, `ViewModels`, `Models`, `Services`, `Data`, `Resources` 폴더를 동일하게 만듭니다.

둘 중 어느 방식도 기능 구현에는 차이가 없습니다. 이미 준비된 문서와 빌드 규칙을 활용하려면 이 골격을 열어 진행하는 쪽이 편합니다.
