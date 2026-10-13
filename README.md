# Quick Open File (Visual Studio 2022 / 2026 확장)

Visual Assist 의 **Open File in Solution**, **Open Corresponding File (헤더 ↔ 소스)**, **Goto Implementation/Declaration**, **VA Navigation Bar** 기능을 재현한 VSIX 확장입니다.

## 기능

### 1. Open File in Solution — `Alt+Shift+O`
- 솔루션의 모든 프로젝트 파일 목록을 대화상자 중앙에 표시합니다.
- 목록 상단에 **이름 / 파일 위치 / 프로젝트** 제목 줄이 고정되어 스크롤해도 항상 보입니다. (경계를 드래그해 컬럼 폭 조절 가능)
- 목록에는 행/열 구분선이 그려져 있어 각 줄과 컬럼을 쉽게 구분할 수 있습니다.
- 하단 입력창에 키워드를 입력하면 바로 필터링됩니다.
  | 입력 | 의미 |
  |---|---|
  | `render h` | 공백 = AND. 파일 이름에 `render` 와 `h` 가 모두 포함 |
  | `render -test` | `-` 로 시작하면 제외 |
  | `engine\core` 또는 `engine/core` | `\` `/` 가 포함되면 파일 이름이 아니라 상대 경로 전체에서 검색 |
- 정렬: 이름 완전 일치 → 이름이 키워드로 시작 → 그 외, 같은 순위는 이름순.
- `Enter` / 더블클릭: 선택한 파일 열기 (`Shift+↑↓`, `Ctrl/Shift+클릭`으로 여러 개 선택 가능)
- `↑ ↓ PgUp PgDn Ctrl+Home Ctrl+End`: 입력창에서 바로 목록 이동
- `F5`: 파일 목록 새로고침, `Esc`: 닫기
- 마지막 검색어와 창 크기를 기억합니다.
- 파일 목록은 캐시되며 솔루션/프로젝트 열기·닫기, 파일 추가·삭제·이름 변경 시 자동으로 갱신됩니다.
- C++ 프로젝트의 *External Dependencies* (시스템 헤더)는 제외합니다.
- 프로젝트가 없는 폴더 열기(Open Folder / CMake) 모드에서는 폴더를 직접 스캔합니다.

### 2. Open Corresponding File — `Alt+O`
C/C++ 파일에서 같은 이름의 헤더 ↔ 소스로 이동합니다.
- 헤더: `.h .hpp .hh .hxx .h++ .inl .ipp .tpp`
- 소스: `.cpp .c .cc .cxx .c++ .cp .m .mm .cu`
- 검색 순서
  1. 같은 폴더
  2. 솔루션 파일 중 같은 이름 (현재 파일과 경로가 가장 비슷한 것 우선)
  3. 상위 폴더의 `include / inc / src / source / public / private` 형제 폴더
- 코드 편집기 우클릭 메뉴와 **Tools** 메뉴에도 추가됩니다 (C/C++ 문서에서만 표시).

### 3. Goto Implementation/Declaration — `Alt+G`
커서가 위치한 심볼(변수, 함수, 클래스 등)의 선언부 ↔ 구현부로 이동합니다.
- C/C++: 사용처·선언 위 → 구현(정의)으로, 구현 위 → 선언으로 이동 (`Edit.GoToDefinition` → `Edit.GoToDeclaration`)
- 그 외 언어(C# 등): 사용처 → 정의로, 선언(인터페이스/추상 멤버) 위 → 구현으로 이동 (`Edit.GoToDefinition` → `Edit.GoToImplementation`)
- 첫 번째 명령으로 커서가 움직이지 않으면 두 번째 명령을 시도합니다. 둘 다 실패하면 상태 표시줄에 알려 줍니다.
- 코드 편집기 우클릭 메뉴와 **Tools** 메뉴에도 추가됩니다.

### 4. Navigation Bar (C/C++, C#) — 함수 목록 `Alt+M`
Visual Assist 의 VA Navigation Bar 처럼 C/C++ 와 C# 편집기 위쪽에 막대를 표시합니다.
- **Go 버튼 (맨 오른쪽)**: `Alt+G` 와 같습니다. 커서 위치 심볼의 선언부 ↔ 구현부로 이동합니다.
- **Context 칸 (왼쪽)**: 커서가 있는 범위를 표시합니다. (예: `Foo::Bar`, 클래스 안이면 `Foo`, 밖이면 `(global scope)`)
  - 클릭하거나 `Alt+M` 을 누르면 파일 안의 클래스/함수 목록이 열립니다 (List Methods in File). 입력해서 필터링하고 `Enter` / 더블클릭으로 이동합니다.
- **Definition 칸 (오른쪽)**: 커서가 있는 심볼(변수, 함수, 클래스 등)의 선언을 표시합니다. (예: `int m_count`, `auto` 변수는 실제 형)
- 막대 표시/숨김: **Tools > Show Navigation Bar** (설정은 저장됨)
- 동작 원리:
  - Context 칸과 함수 목록은 자체 경량 파서로 백그라운드 스레드에서 계산합니다 (VS 코드 모델을 쓰지 않아 UI 를 멈추지 않고, 프로젝트에 없는 파일에서도 동작). 주석·문자열·전처리기를 건너뛰고 중괄호 범위를 추적하며, `#if` / `#else` 는 첫 번째 분기만 읽습니다. 복잡한 매크로에서는 범위가 틀릴 수 있습니다.
  - Definition 칸은 VS Quick Info(마우스 툴팁) 정보를 사용합니다. 솔루션 로드가 끝난 뒤 포커스를 가진 편집기에서만 조회하며, IntelliSense 가 꺼져 있거나 분석 중이면 비어 있을 수 있습니다.
  - 화면에 보이지 않는 탭(시작 시 복원된 뒤쪽 탭 등)은 보일 때까지 아무 작업도 하지 않습니다.
- VS 자체 탐색 막대와 겹쳐 보이면 **Tools > Options > Text Editor > C/C++ > General > Navigation bar** (C# 은 **Text Editor > C# > General > Navigation bar**) 를 끄면 됩니다.
- C#: Context 칸은 네임스페이스를 생략해 `Foo.Bar` 로 표시하고, 함수 목록에는 생성자·속성·이벤트도 포함됩니다. Definition 칸은 VS 툴팁의 `(local variable)` 같은 분류 표시를 떼고 `int count` 처럼 표시합니다.

> 단축키는 **Tools > Options > Environment > Keyboard** 에서 `QuickOpenFile.OpenFileInSolution`, `QuickOpenFile.ToggleHeaderSource`, `QuickOpenFile.GotoImplementationDeclaration`, `QuickOpenFile.ListMethodsInFile` 으로 검색해 변경할 수 있습니다.
> Visual Assist 가 함께 설치되어 있으면 같은 단축키가 충돌하므로 둘 중 하나를 변경하세요.

## 빌드

요구 사항: Visual Studio 2022 이상 (MSBuild), 인터넷 연결 (최초 NuGet 복원).
"Visual Studio 확장 개발" 워크로드는 필요하지 않습니다 (`Microsoft.VSSDK.BuildTools` NuGet 사용).

```bash
"C:/Program Files/Microsoft Visual Studio/2022/Enterprise/MSBuild/Current/Bin/MSBuild.exe" QuickOpenFile.sln -restore -p:Configuration=Release
```

결과물: `QuickOpenFile/bin/Release/net472/QuickOpenFile.vsix`

## 설치

`QuickOpenFile.vsix` 를 더블클릭하면 VSIX Installer 가 실행됩니다 (VS 2022 / VS 2026, x64 · ARM64).

## 소스 구조

| 파일 | 역할 |
|---|---|
| `QuickOpenFilePackage.cs` | AsyncPackage, 명령 등록, 헤더/소스 전환 명령, 문서 열기 |
| `QuickOpenFilePackage.vsct` | 메뉴 / 단축키 정의 |
| `GotoDeclImplCommand.cs` | 선언부 ↔ 구현부 이동 (Alt+G) |
| `NavigationBar/NavigationBarMargin.cs` | 에디터 위쪽 Navigation Bar (Context / Definition 칸) |
| `NavigationBar/MethodListPopup.cs` | 파일의 함수 목록 팝업 (Alt+M) |
| `NavigationBar/ScopeParser.cs` | C++ / C# 경량 파서: 네임스페이스/클래스/함수 범위 계산 (백그라운드 스레드) |
| `NavigationBar/QuickInfoReader.cs` | Quick Info 에서 심볼 선언 텍스트 추출 |
| `NavigationBar/NavigationBarSettings.cs` | 막대 표시 여부 저장 |
| `QuickOpenDialog.cs` | 파일 목록 + 필터 입력 대화상자 (WPF, VS 테마 적용) |
| `SolutionFileIndex.cs` | IVsHierarchy 순회로 솔루션 파일 수집, 캐시, 변경 이벤트 처리 |
| `FileFilter.cs` | 필터 / 정렬 규칙 |
| `HeaderSourceResolver.cs` | 헤더 ↔ 소스 대응 파일 탐색 |
