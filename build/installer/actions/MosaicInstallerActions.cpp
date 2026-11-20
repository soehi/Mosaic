// Mosaic 安装器自定义动作（原生 Win32/MSI API + BCrypt）。
//
// 这里的动作全部立即执行（Type 1），只依赖 Windows Installer 会话、MSI Binary 表里
// 内置的载荷清单和当前用户的文件系统，不依赖安装目录中的 .NET 运行时或应用文件，
// 因此即使整个安装目录被删除也能独立完成状态检测、扫描与修复路由。

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <bcrypt.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <winver.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <cwctype>
#include <string>
#include <vector>

#pragma comment(lib, "msi.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "version.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "advapi32.lib")

namespace {

const wchar_t* kManifestBinary = L"MosaicPayloadManifest";
const wchar_t* kDefaultFolderSuffix = L"Programs\\Mosaic";

std::wstring GetProperty(MSIHANDLE hInstall, const wchar_t* name)
{
    DWORD length = 0;
    UINT status = MsiGetPropertyW(hInstall, name, L"", &length);
    if (status != ERROR_SUCCESS && status != ERROR_MORE_DATA)
        return std::wstring();
    if (length == 0)
        return std::wstring();
    std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
    DWORD size = static_cast<DWORD>(buffer.size());
    status = MsiGetPropertyW(hInstall, name, buffer.data(), &size);
    if (status != ERROR_SUCCESS)
        return std::wstring();
    return std::wstring(buffer.data(), size);
}

void SetProperty(MSIHANDLE hInstall, const wchar_t* name, const std::wstring& value)
{
    MsiSetPropertyW(hInstall, name, value.c_str());
}

void LogInfo(MSIHANDLE hInstall, const std::wstring& message)
{
    PMSIHANDLE record = MsiCreateRecord(1);
    MsiRecordSetStringW(record, 0, message.c_str());
    MsiProcessMessage(hInstall, INSTALLMESSAGE_INFO, record);
}

void ReportError(MSIHANDLE hInstall, const std::wstring& message)
{
    // 错误必须走标准错误消息：静默安装得到非 0 退出码，交互安装显示错误页。
    // 只设置属性会把失败留在日志里，而 /qn 仍以 0 返回。
    PMSIHANDLE record = MsiCreateRecord(1);
    MsiRecordSetStringW(record, 0, message.c_str());
    MsiProcessMessage(hInstall, INSTALLMESSAGE_ERROR, record);
    LogInfo(hInstall, message);
}

bool IsRebootPending(MSIHANDLE hInstall)
{
    if (!GetProperty(hInstall, L"REBOOT").empty())
        return true;
    return MsiGetMode(hInstall, MSIRUNMODE_REBOOTATEND) || MsiGetMode(hInstall, MSIRUNMODE_REBOOTNOW);
}

std::wstring Trim(const std::wstring& value)
{
    size_t begin = 0;
    size_t end = value.size();
    while (begin < end && (value[begin] == L' ' || value[begin] == L'\t' || value[begin] == L'\r' || value[begin] == L'\n'))
        ++begin;
    while (end > begin && (value[end - 1] == L' ' || value[end - 1] == L'\t' || value[end - 1] == L'\r' || value[end - 1] == L'\n'))
        --end;
    return value.substr(begin, end - begin);
}

std::wstring ToLower(std::wstring value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    return value;
}

std::wstring TrimEndingSeparators(const std::wstring& value)
{
    std::wstring result = value;
    while (result.size() > 1 && (result.back() == L'\\' || result.back() == L'/'))
        result.pop_back();
    return result;
}

std::wstring Combine(const std::wstring& root, const std::wstring& relative)
{
    std::wstring result = TrimEndingSeparators(root);
    if (!relative.empty())
    {
        result.push_back(L'\\');
        result += relative;
    }
    return result;
}

bool IsPathEqual(const std::wstring& left, const std::wstring& right)
{
    return ToLower(TrimEndingSeparators(left)) == ToLower(TrimEndingSeparators(right));
}

bool IsPathInside(const std::wstring& child, const std::wstring& parent)
{
    std::wstring c = ToLower(TrimEndingSeparators(child));
    std::wstring p = ToLower(TrimEndingSeparators(parent));
    if (p.empty() || c.size() <= p.size())
        return false;
    if (c.compare(0, p.size(), p) != 0)
        return false;
    return c[p.size()] == L'\\';
}

bool ParseVersion(const std::wstring& text, std::vector<unsigned int>& parts)
{
    parts.clear();
    std::wstring current;
    for (size_t index = 0; index <= text.size(); ++index)
    {
        if (index == text.size() || text[index] == L'.')
        {
            if (current.empty())
                return false;
            parts.push_back(static_cast<unsigned int>(wcstoul(current.c_str(), nullptr, 10)));
            current.clear();
        }
        else if (iswdigit(text[index]))
        {
            current.push_back(text[index]);
        }
        else
        {
            return false;
        }
    }
    return !parts.empty();
}

int CompareVersions(const std::wstring& left, const std::wstring& right)
{
    std::vector<unsigned int> a;
    std::vector<unsigned int> b;
    if (!ParseVersion(left, a) || !ParseVersion(right, b))
    {
        int result = ToLower(left).compare(ToLower(right));
        return result < 0 ? -1 : (result > 0 ? 1 : 0);
    }
    const size_t count = (std::max)(a.size(), b.size());
    for (size_t index = 0; index < count; ++index)
    {
        unsigned int x = index < a.size() ? a[index] : 0;
        unsigned int y = index < b.size() ? b[index] : 0;
        if (x != y)
            return x < y ? -1 : 1;
    }
    return 0;
}

std::wstring FormatCount(int value)
{
    wchar_t buffer[32];
    swprintf_s(buffer, L"%d", value);
    return buffer;
}

bool GetLocalAppData(std::wstring& path)
{
    wchar_t buffer[MAX_PATH * 2];
    HRESULT hr = SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, buffer);
    if (FAILED(hr))
        return false;
    path = buffer;
    return !path.empty();
}

std::wstring DefaultInstallFolder()
{
    std::wstring localAppData;
    if (!GetLocalAppData(localAppData))
        return std::wstring();
    return Combine(localAppData, kDefaultFolderSuffix);
}

std::wstring DataRootFolder()
{
    std::wstring localAppData;
    if (!GetLocalAppData(localAppData))
        return std::wstring();
    return Combine(localAppData, L"Mosaic");
}

bool ReadBinaryStream(MSIHANDLE hInstall, const wchar_t* name, std::string& content)
{
    content.clear();
    MSIHANDLE database = MsiGetActiveDatabase(hInstall);
    if (database == 0)
        return false;
    std::wstring query = L"SELECT `Data` FROM `Binary` WHERE `Name`='";
    query += name;
    query += L"'";
    MSIHANDLE view = 0;
    if (MsiDatabaseOpenViewW(database, query.c_str(), &view) != ERROR_SUCCESS)
    {
        MsiCloseHandle(database);
        return false;
    }
    bool ok = false;
    if (MsiViewExecute(view, 0) == ERROR_SUCCESS)
    {
        MSIHANDLE record = 0;
        if (MsiViewFetch(view, &record) == ERROR_SUCCESS && record != 0)
        {
            DWORD size = MsiRecordDataSize(record, 1);
            if (size > 0)
            {
                content.resize(size);
                DWORD offset = 0;
                DWORD remaining = size;
                bool readOk = true;
                while (remaining > 0)
                {
                    DWORD chunk = remaining;
                    if (MsiRecordReadStream(record, 1, &content[offset], &chunk) != ERROR_SUCCESS || chunk == 0)
                    {
                        readOk = false;
                        break;
                    }
                    offset += chunk;
                    remaining -= chunk;
                }
                content.resize(offset);
                ok = readOk && offset == size;
            }
            MsiCloseHandle(record);
        }
    }
    MsiCloseHandle(view);
    // MsiGetActiveDatabase 返回的数据库句柄同样需要关闭，否则 MSI 会在动作结束时
    // 报告 2769（自定义动作未关闭句柄），0.17.4 的每次扫描都会留下该警告。
    MsiCloseHandle(database);
    return ok;
}

std::wstring Utf8ToWide(const std::string& text)
{
    if (text.empty())
        return std::wstring();
    int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (length <= 0)
        return std::wstring();
    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), &result[0], length);
    return result;
}

bool TryReadFileHash(const std::wstring& path, uint64_t& size, std::wstring& hash, DWORD& error)
{
    hash.clear();
    size = 0;
    error = ERROR_SUCCESS;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        error = GetLastError();
        return false;
    }
    LARGE_INTEGER fileSize;
    if (!GetFileSizeEx(file, &fileSize))
    {
        error = GetLastError();
        CloseHandle(file);
        return false;
    }
    size = static_cast<uint64_t>(fileSize.QuadPart);

    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hashHandle = nullptr;
    bool ok = false;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0 &&
        BCryptCreateHash(algorithm, &hashHandle, nullptr, 0, nullptr, 0, 0) == 0)
    {
        std::vector<unsigned char> buffer(64 * 1024);
        bool readOk = true;
        for (;;)
        {
            DWORD read = 0;
            if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr))
            {
                error = GetLastError();
                readOk = false;
                break;
            }
            if (read == 0)
                break;
            if (BCryptHashData(hashHandle, buffer.data(), read, 0) != 0)
            {
                error = GetLastError();
                readOk = false;
                break;
            }
        }
        if (readOk)
        {
            unsigned char digest[32];
            if (BCryptFinishHash(hashHandle, digest, sizeof(digest), 0) == 0)
            {
                static const wchar_t digits[] = L"0123456789ABCDEF";
                wchar_t hex[65];
                for (size_t index = 0; index < 32; ++index)
                {
                    hex[index * 2] = digits[digest[index] >> 4];
                    hex[index * 2 + 1] = digits[digest[index] & 0xF];
                }
                hex[64] = L'\0';
                hash = hex;
                ok = true;
            }
            else
            {
                error = GetLastError();
            }
        }
    }
    else
    {
        error = GetLastError();
    }
    if (hashHandle)
        BCryptDestroyHash(hashHandle);
    if (algorithm)
        BCryptCloseAlgorithmProvider(algorithm, 0);
    CloseHandle(file);
    return ok;
}

bool IsReparsePoint(const std::wstring& path)
{
    DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}

// ---------------------------------------------------------------------------
// 载荷清单
// ---------------------------------------------------------------------------

struct ManifestFile
{
    std::wstring relativePath;
    uint64_t size = 0;
    std::wstring sha256;
    std::wstring component;
    std::wstring fileId;
};

struct ManifestShortcut
{
    std::wstring location; // desktop / programmenu
    std::wstring relativeLink;
    std::wstring relativeTarget;
};

struct Manifest
{
    std::vector<ManifestFile> files;
    std::vector<ManifestShortcut> shortcuts;
    std::wstring error;
};

std::vector<std::wstring> SplitTabs(const std::wstring& line)
{
    std::vector<std::wstring> fields;
    size_t start = 0;
    for (;;)
    {
        size_t tab = line.find(L'\t', start);
        if (tab == std::wstring::npos)
        {
            fields.push_back(line.substr(start));
            break;
        }
        fields.push_back(line.substr(start, tab - start));
        start = tab + 1;
    }
    return fields;
}

bool IsSafeRelativePath(const std::wstring& relative)
{
    if (relative.empty() || relative.size() > 240)
        return false;
    if (relative.find(L':') != std::wstring::npos)
        return false;
    if (relative[0] == L'\\' || relative[0] == L'/')
        return false;
    size_t start = 0;
    while (start <= relative.size())
    {
        size_t separator = relative.find_first_of(L"\\/", start);
        std::wstring segment = relative.substr(start, separator == std::wstring::npos ? std::wstring::npos : separator - start);
        if (segment.empty() || segment == L"." || segment == L"..")
            return false;
        if (separator == std::wstring::npos)
            break;
        start = separator + 1;
    }
    return true;
}

Manifest LoadManifest(MSIHANDLE hInstall)
{
    Manifest manifest;
    std::string raw;
    if (!ReadBinaryStream(hInstall, kManifestBinary, raw) || raw.empty())
    {
        manifest.error = L"安装包内置的载荷清单缺失或无法读取。";
        return manifest;
    }
    std::wstring text = Utf8ToWide(raw);
    size_t position = 0;
    bool sawHeader = false;
    while (position < text.size())
    {
        size_t newline = text.find(L'\n', position);
        std::wstring line = newline == std::wstring::npos ? text.substr(position) : text.substr(position, newline - position);
        position = newline == std::wstring::npos ? text.size() : newline + 1;
        if (!line.empty() && line.back() == L'\r')
            line.pop_back();
        if (line.empty())
            continue;
        std::vector<std::wstring> fields = SplitTabs(line);
        if (!sawHeader)
        {
            if (fields.size() != 1 || fields[0] != L"mosaic-manifest-v1")
            {
                manifest.error = L"安装包内置的载荷清单版本不受支持。";
                return manifest;
            }
            sawHeader = true;
            continue;
        }
        if (fields[0] == L"file")
        {
            if (fields.size() < 6 || !IsSafeRelativePath(fields[1]))
            {
                manifest.error = L"安装包内置的载荷清单包含无效路径。";
                return manifest;
            }
            ManifestFile file;
            file.relativePath = fields[1];
            file.size = _wcstoui64(fields[2].c_str(), nullptr, 10);
            file.sha256 = ToLower(fields[3]);
            file.component = fields[4];
            file.fileId = fields[5];
            if (file.sha256.size() != 64)
            {
                manifest.error = L"安装包内置的载荷清单包含无效校验值。";
                return manifest;
            }
            manifest.files.push_back(file);
        }
        else if (fields[0] == L"shortcut")
        {
            if (fields.size() < 4 || (fields[1] != L"desktop" && fields[1] != L"programmenu") ||
                !IsSafeRelativePath(fields[2]) || !IsSafeRelativePath(fields[3]))
            {
                manifest.error = L"安装包内置的载荷清单包含无效快捷方式条目。";
                return manifest;
            }
            ManifestShortcut shortcut;
            shortcut.location = fields[1];
            shortcut.relativeLink = fields[2];
            shortcut.relativeTarget = fields[3];
            manifest.shortcuts.push_back(shortcut);
        }
        else
        {
            manifest.error = L"安装包内置的载荷清单包含未知条目。";
            return manifest;
        }
    }
    if (!sawHeader)
    {
        manifest.error = L"安装包内置的载荷清单为空。";
        return manifest;
    }
    return manifest;
}

// ---------------------------------------------------------------------------
// 安装状态
// ---------------------------------------------------------------------------

struct ProductCandidate
{
    std::wstring productCode;
    std::wstring version;
    std::wstring location;
    std::wstring name;
};

std::wstring GetProductInfo(const std::wstring& productCode, const wchar_t* property)
{
    DWORD length = 0;
    UINT status = MsiGetProductInfoW(productCode.c_str(), property, L"", &length);
    if (status != ERROR_SUCCESS && status != ERROR_MORE_DATA)
        return std::wstring();
    if (length == 0)
        return std::wstring();
    std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
    DWORD size = static_cast<DWORD>(buffer.size());
    if (MsiGetProductInfoW(productCode.c_str(), property, buffer.data(), &size) != ERROR_SUCCESS)
        return std::wstring();
    return std::wstring(buffer.data(), size);
}

bool ExecutableIdentityMatches(const std::wstring& executable)
{
    DWORD handle = 0;
    DWORD size = GetFileVersionInfoSizeW(executable.c_str(), &handle);
    if (size == 0)
        return false;
    std::vector<unsigned char> data(size);
    if (!GetFileVersionInfoW(executable.c_str(), 0, size, data.data()))
        return false;
    struct Translation
    {
        WORD language;
        WORD codePage;
    };
    Translation* translation = nullptr;
    UINT translationSize = 0;
    if (!VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&translation), &translationSize) ||
        translationSize < sizeof(Translation))
        return false;
    wchar_t query[128];
    swprintf_s(query, L"\\StringFileInfo\\%04x%04x\\ProductName", translation[0].language, translation[0].codePage);
    wchar_t* productName = nullptr;
    UINT productNameSize = 0;
    if (!VerQueryValueW(data.data(), query, reinterpret_cast<void**>(&productName), &productNameSize) || productNameSize == 0)
        return false;
    return ToLower(std::wstring(productName)).find(L"mosaic") != std::wstring::npos;
}

bool ResolveInstalledLocation(const ProductCandidate& product, std::wstring& location, bool& inheritedFromLegacyDefault)
{
    inheritedFromLegacyDefault = false;
    if (!product.location.empty())
    {
        // 已登记的安装位置即使用户删除了整个目录也必须沿用：维护时在原位置恢复，
        // 不能用「目录不存在」推断为未安装，否则删除目录后就永远无法修复。
        location = TrimEndingSeparators(product.location);
        return true;
    }
    // 0.17.2 及更早版本没有登记 ARPINSTALLLOCATION，位置固定为默认目录。只有确认
    // 产品族身份（产品名或默认目录中的可执行文件）后才回落到该目录。
    std::wstring fallback = DefaultInstallFolder();
    if (fallback.empty())
        return false;
    bool identity = ToLower(Trim(product.name)) == L"mosaic";
    std::wstring executable = Combine(fallback, L"Mosaic.Desktop.exe");
    if (!identity && PathFileExistsW(executable.c_str()))
        identity = ExecutableIdentityMatches(executable);
    if (!identity)
        return false;
    location = fallback;
    inheritedFromLegacyDefault = true;
    return true;
}

enum class InstallState
{
    Fresh,
    Maintenance,
    Upgrade,
    BlockedSameVersion,
    BlockedNewer,
    Unknown
};

bool DetectState(MSIHANDLE hInstall, InstallState& state, ProductCandidate& target, std::wstring& error)
{
    state = InstallState::Fresh;
    error.clear();
    std::wstring upgradeCode = GetProperty(hInstall, L"MOSAICUPGRADECODE");
    std::wstring productCode = GetProperty(hInstall, L"ProductCode");
    std::wstring productVersion = GetProperty(hInstall, L"ProductVersion");
    if (upgradeCode.empty() || productCode.empty() || productVersion.empty())
    {
        state = InstallState::Unknown;
        error = L"安装包缺少产品标识，无法检测已安装版本。";
        return false;
    }

    ProductCandidate same;
    bool foundSame = false;
    ProductCandidate upgrade;
    bool foundUpgrade = false;

    wchar_t codeBuffer[64];
    for (DWORD index = 0; MsiEnumRelatedProductsW(upgradeCode.c_str(), 0, index, codeBuffer) == ERROR_SUCCESS; ++index)
    {
        std::wstring relatedCode = codeBuffer;
        ProductCandidate candidate;
        candidate.productCode = relatedCode;
        candidate.version = GetProductInfo(relatedCode, INSTALLPROPERTY_VERSIONSTRING);
        candidate.location = GetProductInfo(relatedCode, INSTALLPROPERTY_INSTALLLOCATION);
        candidate.name = GetProductInfo(relatedCode, INSTALLPROPERTY_PRODUCTNAME);
        if (relatedCode == productCode)
        {
            same = candidate;
            foundSame = true;
            continue;
        }
        if (CompareVersions(candidate.version, productVersion) == 0)
        {
            state = InstallState::BlockedSameVersion;
            target = candidate;
            error = L"已安装相同版本的 Mosaic（产品代码 " + relatedCode + L"），但安装包的产品代码不同。请使用与已安装版本相同的安装包进行修复。";
            return true;
        }
        if (CompareVersions(candidate.version, productVersion) > 0)
        {
            state = InstallState::BlockedNewer;
            target = candidate;
            error = L"已安装更新版本的 Mosaic（" + candidate.version + L"），不能降级安装 " + productVersion + L"。";
            return true;
        }
        if (!foundUpgrade || CompareVersions(candidate.version, upgrade.version) > 0)
        {
            upgrade = candidate;
            foundUpgrade = true;
        }
    }

    if (foundSame)
    {
        if (CompareVersions(same.version, productVersion) == 0)
        {
            state = InstallState::Maintenance;
            target = same;
            return true;
        }
        state = InstallState::Unknown;
        error = L"已安装的 Mosaic 版本（" + same.version + L"）与安装包版本（" + productVersion + L"）不一致，无法进入维护。";
        return false;
    }
    if (foundUpgrade)
    {
        state = InstallState::Upgrade;
        target = upgrade;
        return true;
    }
    state = InstallState::Fresh;
    return true;
}

} // namespace

// ---------------------------------------------------------------------------
// 导出动作
// ---------------------------------------------------------------------------

extern "C" __declspec(dllexport) UINT __stdcall DetectInstallState(MSIHANDLE hInstall)
{
    InstallState state = InstallState::Unknown;
    ProductCandidate target;
    std::wstring error;
    DetectState(hInstall, state, target, error);

    const wchar_t* stateName = L"Unknown";
    switch (state)
    {
    case InstallState::Fresh: stateName = L"Fresh"; break;
    case InstallState::Maintenance: stateName = L"Maintenance"; break;
    case InstallState::Upgrade: stateName = L"Upgrade"; break;
    case InstallState::BlockedSameVersion: stateName = L"BlockedSameVersion"; break;
    case InstallState::BlockedNewer: stateName = L"BlockedNewer"; break;
    case InstallState::Unknown: stateName = L"Unknown"; break;
    }
    SetProperty(hInstall, L"MOSAICSTATE", stateName);
    SetProperty(hInstall, L"MOSAICSTATEERROR", error);
    // 身份冲突或检测失败时阻止静默安装继续，避免误修复或双份登记。
    if (state == InstallState::BlockedSameVersion || state == InstallState::BlockedNewer ||
        state == InstallState::Unknown)
        SetProperty(hInstall, L"MOSAICBLOCKED", L"1");
    else
        SetProperty(hInstall, L"MOSAICBLOCKED", L"");
    if (!target.productCode.empty())
    {
        SetProperty(hInstall, L"MOSAICINSTALLEDPRODUCTCODE", target.productCode);
        SetProperty(hInstall, L"MOSAICINSTALLEDVERSION", target.version);
        SetProperty(hInstall, L"MOSAICINSTALLEDLOCATION", target.location);
    }

    // 首次安装预填默认安装位置；静默安装若显式传入 INSTALLFOLDER 则以传入值为准。
    if (state == InstallState::Fresh && GetProperty(hInstall, L"MOSAICINSTALLPATH").empty() &&
        GetProperty(hInstall, L"INSTALLFOLDER").empty())
    {
        std::wstring fallback = DefaultInstallFolder();
        if (!fallback.empty())
            SetProperty(hInstall, L"MOSAICINSTALLPATH", fallback);
    }

    if (state == InstallState::Maintenance || state == InstallState::Upgrade)
    {
        std::wstring location;
        bool fromLegacyDefault = false;
        if (ResolveInstalledLocation(target, location, fromLegacyDefault))
        {
            SetProperty(hInstall, L"MOSAICINSTALLEDLOCATION", location);
            SetProperty(hInstall, L"INSTALLFOLDER", location);
            SetProperty(hInstall, L"MOSAICLOCATIONINHERITED", L"1");
            if (fromLegacyDefault)
                SetProperty(hInstall, L"MOSAICLOCATIONLEGACYFALLBACK", L"1");
            LogInfo(hInstall, L"Mosaic: 已登记安装位置 " + location);
        }
        else
        {
            SetProperty(hInstall, L"MOSAICLOCATIONMISSING", L"1");
            std::wstring message = state == InstallState::Maintenance
                ? L"已安装的 Mosaic 未登记安装位置，且默认位置中找不到可确认身份的程序文件。请通过「应用和功能」修复或重新安装。"
                : L"已安装的 Mosaic 未登记安装位置，且默认位置中找不到可确认身份的程序文件，无法安全地原位升级。请通过「应用和功能」修复或重新安装。";
            SetProperty(hInstall, L"MOSAICSTATUSMESSAGE", message);
            // 位置未能确认时按身份冲突处理：既不能扫描或修复错误目录，也不能在默认目录另装一份。
            SetProperty(hInstall, L"MOSAICSTATE", L"Unknown");
            SetProperty(hInstall, L"MOSAICSTATEERROR", message);
            SetProperty(hInstall, L"MOSAICBLOCKED", L"1");
            LogInfo(hInstall, L"Mosaic: 无法确认已安装位置。");
        }
    }
    LogInfo(hInstall, std::wstring(L"Mosaic: 检测状态 ") + stateName);
    return ERROR_SUCCESS;
}

// 卸载前恢复已登记的安装位置。DetectInstallState 只在安装/维护时执行，INSTALLFOLDER
// 在卸载会话中会退回包内默认目录（%LOCALAPPDATA%\Programs\Mosaic），导致自定义位置的
// 文件全部残留；这里在 CostFinalize 之前把目录表目标改回产品实际登记的位置。
extern "C" __declspec(dllexport) UINT __stdcall RestoreInstallFolderForRemoval(MSIHANDLE hInstall)
{
    std::wstring productCode = GetProperty(hInstall, L"ProductCode");
    if (productCode.empty())
        return ERROR_SUCCESS;
    std::wstring location = TrimEndingSeparators(Trim(GetProductInfo(productCode, INSTALLPROPERTY_INSTALLLOCATION)));
    if (location.empty() || PathIsRelativeW(location.c_str()))
    {
        // 0.17.2 及更早版本未登记位置，卸载保持默认目录语义。
        LogInfo(hInstall, L"Mosaic: 卸载时未登记安装位置，按默认目录移除。");
        return ERROR_SUCCESS;
    }
    SetProperty(hInstall, L"INSTALLFOLDER", location);
    SetProperty(hInstall, L"MOSAICINSTALLEDLOCATION", location);
    LogInfo(hInstall, L"Mosaic: 卸载位置 " + location);
    return ERROR_SUCCESS;
}

namespace {

struct ScanOutcome
{
    int missing = 0;
    int damaged = 0;
    int errors = 0;
    int checkedFiles = 0;
    std::wstring details;
    std::wstring errorMessage;
};

void AppendDetail(ScanOutcome& outcome, const std::wstring& text)
{
    if (outcome.details.size() > 600)
        return;
    if (!outcome.details.empty())
        outcome.details += L"；";
    outcome.details += text;
}

std::wstring ShortcutLinkPath(const std::wstring& location, const std::wstring& relativeLink)
{
    int folder = location == L"desktop" ? CSIDL_DESKTOPDIRECTORY : CSIDL_PROGRAMS;
    wchar_t buffer[MAX_PATH * 2];
    if (FAILED(SHGetFolderPathW(nullptr, folder, nullptr, SHGFP_TYPE_CURRENT, buffer)))
        return std::wstring();
    return Combine(buffer, relativeLink);
}

std::wstring ShortcutDisplayName(const ManifestShortcut& shortcut)
{
    return (shortcut.location == L"desktop" ? std::wstring(L"桌面快捷方式 ") : std::wstring(L"开始菜单快捷方式 ")) + shortcut.relativeLink;
}

bool CheckShortcut(const ManifestShortcut& shortcut, const std::wstring& expectedTarget, ScanOutcome& outcome, bool& missing)
{
    missing = false;
    std::wstring linkPath = ShortcutLinkPath(shortcut.location, shortcut.relativeLink);
    if (linkPath.empty())
    {
        ++outcome.errors;
        AppendDetail(outcome, L"无法确定快捷方式位置");
        return false;
    }
    if (!PathFileExistsW(linkPath.c_str()))
    {
        missing = true;
        ++outcome.missing;
        AppendDetail(outcome, ShortcutDisplayName(shortcut));
        return false;
    }
    if (IsReparsePoint(linkPath))
    {
        ++outcome.errors;
        AppendDetail(outcome, L"快捷方式经过联接点");
        return false;
    }

    HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool comReady = SUCCEEDED(initialized);
    IShellLinkW* link = nullptr;
    bool ok = false;
    if (comReady && SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_IShellLinkW,
                                               reinterpret_cast<void**>(&link))))
    {
        IPersistFile* persist = nullptr;
        if (SUCCEEDED(link->QueryInterface(IID_IPersistFile, reinterpret_cast<void**>(&persist))))
        {
            if (SUCCEEDED(persist->Load(linkPath.c_str(), STGM_READ)))
            {
                wchar_t targetBuffer[MAX_PATH * 2];
                if (SUCCEEDED(link->GetPath(targetBuffer, MAX_PATH * 2, nullptr, SLGP_UNCPRIORITY)))
                    ok = IsPathEqual(targetBuffer, expectedTarget) ||
                         IsPathEqual(PathFindFileNameW(targetBuffer), PathFindFileNameW(expectedTarget.c_str()));
            }
            persist->Release();
        }
        link->Release();
    }
    if (initialized == S_OK || initialized == S_FALSE)
        CoUninitialize();
    if (!ok)
    {
        ++outcome.damaged;
        AppendDetail(outcome, ShortcutDisplayName(shortcut));
    }
    return ok;
}

std::wstring cornerShortcutName(const ManifestShortcut& shortcut)
{
    return (shortcut.location == L"desktop" ? std::wstring(L"桌面快捷方式 ") : std::wstring(L"开始菜单快捷方式 ")) + shortcut.relativeLink;
}

ScanOutcome ScanInstallRoot(MSIHANDLE hInstall)
{
    ScanOutcome outcome;
    Manifest manifest = LoadManifest(hInstall);
    if (!manifest.error.empty())
    {
        outcome.errorMessage = manifest.error;
        outcome.errors = 1;
        return outcome;
    }
    std::wstring root = GetProperty(hInstall, L"MOSAICINSTALLEDLOCATION");
    if (root.empty())
        root = GetProperty(hInstall, L"INSTALLFOLDER");
    if (root.empty())
    {
        outcome.errorMessage = L"无法确认 Mosaic 的安装位置，未执行完整性检查。";
        outcome.errors = 1;
        return outcome;
    }
    root = TrimEndingSeparators(root);
    if (IsReparsePoint(root))
    {
        outcome.errorMessage = L"安装位置是符号链接或联接点，拒绝在链接目标上执行检查。";
        outcome.errors = 1;
        return outcome;
    }
    bool rootMissing = !PathFileExistsW(root.c_str());
    if (!rootMissing)
    {
        DWORD attributes = GetFileAttributesW(root.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            outcome.errorMessage = L"安装位置不是一个可读取的文件夹。";
            outcome.errors = 1;
            return outcome;
        }
    }

    for (const ManifestFile& file : manifest.files)
    {
        ++outcome.checkedFiles;
        std::wstring path = Combine(root, file.relativePath);
        if (rootMissing)
        {
            ++outcome.missing;
            AppendDetail(outcome, L"缺失 " + file.relativePath);
            continue;
        }
        if (IsReparsePoint(path))
        {
            ++outcome.errors;
            AppendDetail(outcome, L"链接 " + file.relativePath);
            continue;
        }
        uint64_t size = 0;
        std::wstring hash;
        DWORD error = ERROR_SUCCESS;
        if (!TryReadFileHash(path, size, hash, error))
        {
            if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
            {
                ++outcome.missing;
                AppendDetail(outcome, L"缺失 " + file.relativePath);
            }
            else
            {
                ++outcome.errors;
                AppendDetail(outcome, L"无法读取 " + file.relativePath);
            }
            continue;
        }
        if (size != file.size)
        {
            ++outcome.damaged;
            AppendDetail(outcome, L"损坏 " + file.relativePath);
            continue;
        }
        if (ToLower(hash) != file.sha256)
        {
            ++outcome.damaged;
            AppendDetail(outcome, L"校验不符 " + file.relativePath);
        }
    }

    for (const ManifestShortcut& shortcut : manifest.shortcuts)
    {
        bool ignored = false;
        CheckShortcut(shortcut, Combine(root, shortcut.relativeTarget), outcome, ignored);
    }
    return outcome;
}

void PublishScanProperties(MSIHANDLE hInstall, const ScanOutcome& outcome, const wchar_t* prefix)
{
    bool healthy = outcome.errors == 0 && outcome.missing == 0 && outcome.damaged == 0;
    std::wstring status = outcome.errorMessage.empty()
        ? (healthy ? L"Healthy" : (outcome.errors > 0 ? L"Error" : L"Repair"))
        : L"Error";
    std::wstring prefixText = prefix;
    SetProperty(hInstall, (prefixText + L"STATUS").c_str(), status);
    SetProperty(hInstall, (prefixText + L"MISSING").c_str(), FormatCount(outcome.missing));
    SetProperty(hInstall, (prefixText + L"DAMAGED").c_str(), FormatCount(outcome.damaged));
    SetProperty(hInstall, (prefixText + L"ERROR").c_str(), FormatCount(outcome.errors));
    SetProperty(hInstall, (prefixText + L"CHECKED").c_str(), FormatCount(outcome.checkedFiles));
    SetProperty(hInstall, (prefixText + L"DETAIL").c_str(), outcome.details);
    SetProperty(hInstall, (prefixText + L"MESSAGE").c_str(), outcome.errorMessage);

    std::wstring summary;
    if (!outcome.errorMessage.empty())
    {
        summary = outcome.errorMessage;
    }
    else if (outcome.errors > 0)
    {
        summary = L"有 " + FormatCount(outcome.errors) + L" 个文件无法读取或无法确认，完整性检查未通过。请关闭占用文件的程序后重试，或通过「应用和功能」修复。";
    }
    else if (healthy)
    {
        summary = L"已检查 " + FormatCount(outcome.checkedFiles) + L" 个文件，未发现缺失或损坏。";
    }
    else
    {
        summary = L"已检查 " + FormatCount(outcome.checkedFiles) + L" 个文件：";
        if (outcome.missing > 0)
            summary += FormatCount(outcome.missing) + L" 个文件缺失";
        if (outcome.damaged > 0)
        {
            if (outcome.missing > 0)
                summary += L"，";
            summary += FormatCount(outcome.damaged) + L" 个文件损坏";
        }
        if (outcome.errors > 0)
        {
            if (outcome.missing > 0 || outcome.damaged > 0)
                summary += L"，";
            summary += FormatCount(outcome.errors) + L" 个文件无法检查";
        }
        summary += L"。";
    }
    SetProperty(hInstall, (prefixText + L"SUMMARY").c_str(), summary);
}

} // namespace

extern "C" __declspec(dllexport) UINT __stdcall ScanPayload(MSIHANDLE hInstall)
{
    ScanOutcome outcome = ScanInstallRoot(hInstall);
    PublishScanProperties(hInstall, outcome, L"MOSAICSCAN");
    std::wstring state = GetProperty(hInstall, L"MOSAICSTATE");
    std::wstring explicitReinstall = GetProperty(hInstall, L"REINSTALL");

    // 无法读取、清单损坏或位置不可确认属于检查失败：既不能当健康继续，
    // 也不能仅靠设置属性把 /qn 变成成功返回。
    if (!outcome.errorMessage.empty() || outcome.errors > 0)
    {
        SetProperty(hInstall, L"MOSAICREPAIRNEEDED", L"");
        SetProperty(hInstall, L"MOSAICREPAIRAUTOMATIC", L"");
        ReportError(hInstall, L"Mosaic 完整性检查未通过：" + GetProperty(hInstall, L"MOSAICSCANSUMMARY"));
        return ERROR_INSTALL_FAILURE;
    }

    bool healthy = outcome.missing == 0 && outcome.damaged == 0;
    bool needsRepair = state == L"Maintenance" && !healthy;
    SetProperty(hInstall, L"MOSAICMAINTBUTTONTEXT", healthy && explicitReinstall.empty() ? L"完成" : L"修复");
    if (needsRepair)
    {
        SetProperty(hInstall, L"MOSAICREPAIRNEEDED", L"1");
        // 仅当调用方没有显式传入 /f 时自动进入修复；显式 /f 保留其自身的修复语义。
        if (explicitReinstall.empty())
        {
            SetProperty(hInstall, L"REINSTALL", L"ALL");
            SetProperty(hInstall, L"REINSTALLMODE", L"amus");
            SetProperty(hInstall, L"MOSAICREPAIRAUTOMATIC", L"1");
        }
    }
    else
    {
        SetProperty(hInstall, L"MOSAICREPAIRNEEDED", L"");
        SetProperty(hInstall, L"MOSAICREPAIRAUTOMATIC", L"");
    }
    LogInfo(hInstall, L"Mosaic: 扫描 " + GetProperty(hInstall, L"MOSAICSCANSUMMARY"));
    return ERROR_SUCCESS;
}

extern "C" __declspec(dllexport) UINT __stdcall VerifyRepairedPayload(MSIHANDLE hInstall)
{
    ScanOutcome outcome = ScanInstallRoot(hInstall);
    PublishScanProperties(hInstall, outcome, L"MOSAICVERIFY");
    std::wstring status = GetProperty(hInstall, L"MOSAICVERIFYSTATUS");
    std::wstring summary = GetProperty(hInstall, L"MOSAICVERIFYSUMMARY");
    if (status == L"Healthy")
    {
        SetProperty(hInstall, L"MOSAICEXITTITLE", L"修复已完成");
        SetProperty(hInstall, L"MOSAICVERIFYMESSAGE", L"修复已完成，全部受管文件与快捷方式均通过复检。");
        LogInfo(hInstall, L"Mosaic: 修复后核实通过。");
        return ERROR_SUCCESS;
    }
    // 被占用而安排到重启后替换的文件在重启前必然哈希不符：这是「待重启」而不是
    // 「修复成功」，也不是失败；保留 3010 让调用方看到需重启状态。
    if (IsRebootPending(hInstall))
    {
        SetProperty(hInstall, L"MOSAICVERIFYSTATUS", L"PendingReboot");
        SetProperty(hInstall, L"MOSAICEXITTITLE", L"需要重启后完成修复");
        SetProperty(hInstall, L"MOSAICVERIFYMESSAGE",
                    L"部分文件已安排重启后替换，重启前复检不会把它们算作已修复。请重启电脑后再次运行本安装包复查。");
        LogInfo(hInstall, L"Mosaic: 修复后复检待重启，未宣称修复成功。");
        return ERROR_SUCCESS;
    }
    std::wstring message = status == L"Repair"
        ? L"修复后复检仍未通过：" + summary + L" 请重新运行安装包再次修复；若文件被其他程序占用，关闭后重试或重启电脑。"
        : L"修复后无法完成完整性复检：" + summary;
    SetProperty(hInstall, L"MOSAICEXITTITLE", L"修复未通过复检");
    SetProperty(hInstall, L"MOSAICVERIFYMESSAGE", message);
    ReportError(hInstall, message);
    return ERROR_INSTALL_FAILURE;
}

namespace {

std::wstring ExpandEnvironment(const std::wstring& value)
{
    wchar_t buffer[1024];
    DWORD written = ExpandEnvironmentStringsW(value.c_str(), buffer, 1024);
    if (written == 0 || written > 1024)
        return value;
    return buffer;
}

bool IsSystemFolder(const std::wstring& path, std::wstring& reason)
{
    wchar_t windowsDirectory[MAX_PATH * 2];
    if (GetWindowsDirectoryW(windowsDirectory, MAX_PATH * 2) > 0)
    {
        if (IsPathEqual(path, windowsDirectory) || IsPathInside(path, windowsDirectory))
        {
            reason = L"安装位置不能位于 Windows 系统目录内。";
            return true;
        }
    }
    wchar_t programFiles[MAX_PATH * 2];
    if (GetEnvironmentVariableW(L"ProgramFiles", programFiles, MAX_PATH * 2) > 0)
    {
        if (IsPathEqual(path, programFiles) || IsPathInside(path, programFiles))
        {
            reason = L"本产品按当前用户安装，不能使用 Program Files。";
            return true;
        }
    }
    wchar_t programFilesX86[MAX_PATH * 2];
    if (GetEnvironmentVariableW(L"ProgramFiles(x86)", programFilesX86, MAX_PATH * 2) > 0)
    {
        if (IsPathEqual(path, programFilesX86) || IsPathInside(path, programFilesX86))
        {
            reason = L"本产品按当前用户安装，不能使用 Program Files (x86)。";
            return true;
        }
    }
    return false;
}

bool HasReparseAncestor(const std::wstring& path)
{
    std::wstring current = TrimEndingSeparators(path);
    // 从目标路径向上检查已存在的目录，任一环节是联接点/符号链接都拒绝。
    for (;;)
    {
        DWORD attributes = GetFileAttributesW(current.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES)
        {
            if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                return true;
        }
        std::wstring parent = current;
        size_t separator = parent.find_last_of(L'\\');
        if (separator == std::wstring::npos || separator < 2)
            break;
        parent = parent.substr(0, separator);
        if (parent.size() < 3)
            break;
        current = parent;
    }
    return false;
}

// 浏览选择的是父文件夹时追加 Mosaic；已选文件夹本身名为 Mosaic 时不重复追加。
std::wstring AppendMosaicFolder(const std::wstring& path)
{
    std::wstring trimmed = TrimEndingSeparators(path);
    std::wstring leaf = trimmed;
    size_t separator = leaf.find_last_of(L'\\');
    if (separator != std::wstring::npos)
        leaf = leaf.substr(separator + 1);
    if (IsPathEqual(leaf, L"Mosaic"))
        return trimmed;
    return Combine(trimmed, L"Mosaic");
}

bool DirectoryHasEntries(const std::wstring& path)
{
    WIN32_FIND_DATAW data = {};
    std::wstring pattern = Combine(path, L"*");
    HANDLE handle = FindFirstFileW(pattern.c_str(), &data);
    if (handle == INVALID_HANDLE_VALUE)
        return false;
    bool hasEntries = false;
    do
    {
        if (wcscmp(data.cFileName, L".") != 0 && wcscmp(data.cFileName, L"..") != 0)
        {
            hasEntries = true;
            break;
        }
    } while (FindNextFileW(handle, &data));
    FindClose(handle);
    return hasEntries;
}

bool ValidateInstallPath(MSIHANDLE hInstall)
{
    std::wstring input = Trim(GetProperty(hInstall, L"MOSAICINSTALLPATH"));
    // 界面输入按「父文件夹 + Mosaic」规则规范化；静默安装传入的 INSTALLFOLDER 视为最终目录。
    bool appendMosaic = !input.empty();
    SetProperty(hInstall, L"MOSAICPATHERROR", L"");
    if (input.empty())
        input = Trim(GetProperty(hInstall, L"INSTALLFOLDER"));
    if (input.empty())
        input = DefaultInstallFolder();
    if (input.empty())
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"无法确定默认安装位置，请手动选择文件夹。");
        return false;
    }
    if (input.size() >= 2 && input.front() == L'"' && input.back() == L'"')
        input = Trim(input.substr(1, input.size() - 2));
    if (input.size() > 220)
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置过长，请选择更短的文件夹。");
        return false;
    }
    input = ExpandEnvironment(input);
    std::replace(input.begin(), input.end(), L'/', L'\\');
    while (input.size() > 3 && input.back() == L'\\')
        input.pop_back();

    if (input.size() >= 2 && input[0] == L'\\' && input[1] == L'\\')
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置必须是本机文件夹，不能使用网络共享路径。");
        return false;
    }
    if (input.size() < 3 || input[1] != L':' || !iswalpha(input[0]))
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置必须是绝对路径，例如 C:\\Users\\名字\\Programs。");
        return false;
    }
    wchar_t driveRoot[8] = { input[0], L':', L'\\', 0 };
    UINT driveType = GetDriveTypeW(driveRoot);
    if (driveType == DRIVE_REMOTE || driveType == DRIVE_CDROM || driveType == DRIVE_NO_ROOT_DIR || driveType == DRIVE_UNKNOWN)
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置所在的驱动器不可用，必须选择本机固定磁盘。");
        return false;
    }
    if (input.size() == 3)
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置不能是驱动器根目录，请选择一个文件夹。");
        return false;
    }

    std::wstring reason;
    if (IsSystemFolder(input, reason))
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", reason);
        return false;
    }

    std::wstring dataRoot = DataRootFolder();
    if (!dataRoot.empty())
    {
        if (IsPathEqual(input, dataRoot) || IsPathInside(input, dataRoot) || IsPathInside(dataRoot, input))
        {
            SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置不能与用户数据目录（%LOCALAPPDATA%\\Mosaic）重叠，避免升级或修复影响数据。");
            return false;
        }
    }

    // 用户在界面选择的是父文件夹时，Mosaic 安装在其下的 Mosaic 子目录；已经以 Mosaic 结尾时保持不变。
    // 静默安装的 INSTALLFOLDER 本身即最终目录，不再追加。
    if (appendMosaic)
        input = AppendMosaicFolder(input);

    // 追加子目录后必须重新核对隔离规则：父目录为 %LOCALAPPDATA% 时会得到
    // %LOCALAPPDATA%\Mosaic，与用户数据目录完全重合，必须在最终路径上拦截。
    if (!dataRoot.empty())
    {
        if (IsPathEqual(input, dataRoot) || IsPathInside(input, dataRoot) || IsPathInside(dataRoot, input))
        {
            SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置不能与用户数据目录（%LOCALAPPDATA%\\Mosaic）重叠，避免升级或修复影响数据。");
            return false;
        }
    }
    if (IsSystemFolder(input, reason))
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", reason);
        return false;
    }

    if (input.size() > 240)
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"安装位置过长，请选择更短的文件夹。");
        return false;
    }
    if (HasReparseAncestor(input))
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"所选路径经过符号链接或联接点，请选择普通文件夹。");
        return false;
    }
    DWORD attributes = GetFileAttributesW(input.c_str());
    if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
    {
        SetProperty(hInstall, L"MOSAICPATHERROR", L"所选位置已存在同名文件，请换一个文件夹。");
        return false;
    }
    if (GetProperty(hInstall, L"MOSAICSTATE") == L"Fresh" && attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 && DirectoryHasEntries(input))
    {
        SetProperty(hInstall, L"MOSAICPATHERROR",
                    L"该文件夹已存在其他文件，但不是已登记的 Mosaic 安装。请选择其他文件夹，或先备份后再继续。");
        return false;
    }

    SetProperty(hInstall, L"MOSAICINSTALLPATH", input);
    SetProperty(hInstall, L"INSTALLFOLDER", input);
    LogInfo(hInstall, L"Mosaic: 安装位置 " + input);
    return true;
}

} // namespace

extern "C" __declspec(dllexport) UINT __stdcall ValidateInstallFolder(MSIHANDLE hInstall)
{
    ValidateInstallPath(hInstall);
    return ERROR_SUCCESS;
}

// 静默安装使用：路径无效时终止安装，而不是退回到默认目录。
extern "C" __declspec(dllexport) UINT __stdcall EnforceInstallFolder(MSIHANDLE hInstall)
{
    if (ValidateInstallPath(hInstall))
        return ERROR_SUCCESS;
    LogInfo(hInstall, L"Mosaic: 安装位置无效，安装终止。原因：" + GetProperty(hInstall, L"MOSAICPATHERROR"));
    return ERROR_INSTALL_FAILURE;
}

extern "C" __declspec(dllexport) UINT __stdcall RestoreDefaultInstallFolder(MSIHANDLE hInstall)
{
    std::wstring folder = DefaultInstallFolder();
    if (!folder.empty())
        SetProperty(hInstall, L"MOSAICINSTALLPATH", folder);
    return ERROR_SUCCESS;
}

extern "C" __declspec(dllexport) UINT __stdcall BrowseInstallFolder(MSIHANDLE hInstall)
{
    HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    wchar_t displayName[MAX_PATH] = {};
    BROWSEINFOW browse = {};
    browse.hwndOwner = GetActiveWindow();
    browse.pszDisplayName = displayName;
    browse.lpszTitle = L"选择 Mosaic 的安装位置";
    browse.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
    PIDLIST_ABSOLUTE item = SHBrowseForFolderW(&browse);
    bool selected = false;
    if (item != nullptr)
    {
        wchar_t path[MAX_PATH * 2];
        if (SHGetPathFromIDListW(item, path))
        {
            std::wstring folder = AppendMosaicFolder(path);
            SetProperty(hInstall, L"MOSAICINSTALLPATH", folder);
            selected = true;
        }
        CoTaskMemFree(item);
    }
    if (initialized == S_OK || initialized == S_FALSE)
        CoUninitialize();
    if (!selected)
        LogInfo(hInstall, L"Mosaic: 用户取消了文件夹选择。");
    return ERROR_SUCCESS;
}