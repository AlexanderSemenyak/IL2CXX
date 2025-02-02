declare const enum ArgsMarshal {
    Int32 = "i",
    Int32Enum = "j",
    Int64 = "l",
    Int64Enum = "k",
    Float32 = "f",
    Float64 = "d",
    String = "s",
    Char = "s",
    JSObj = "o",
    MONOObj = "m"
}
declare type _ExtraArgsMarshalOperators = "!" | "";
declare type ArgsMarshalString = "" | `${ArgsMarshal}${_ExtraArgsMarshalOperators}` | `${ArgsMarshal}${ArgsMarshal}${_ExtraArgsMarshalOperators}` | `${ArgsMarshal}${ArgsMarshal}${ArgsMarshal}${_ExtraArgsMarshalOperators}` | `${ArgsMarshal}${ArgsMarshal}${ArgsMarshal}${ArgsMarshal}${_ExtraArgsMarshalOperators}`;

interface ManagedPointer {
    __brandManagedPointer: "ManagedPointer";
}
interface NativePointer {
    __brandNativePointer: "NativePointer";
}
interface VoidPtr$1 extends NativePointer {
    __brand: "VoidPtr";
}
interface MonoObject extends ManagedPointer {
    __brandMonoObject: "MonoObject";
}
interface MonoString extends MonoObject {
    __brand: "MonoString";
}
interface MonoArray extends MonoObject {
    __brand: "MonoArray";
}
declare type MonoConfig = {
    isError: false;
    assembly_root: string;
    assets: AllAssetEntryTypes[];
    debug_level?: number;
    enable_debugging?: number;
    fetch_file_cb?: Request;
    globalization_mode: GlobalizationMode;
    diagnostic_tracing?: boolean;
    remote_sources?: string[];
    environment_variables?: {
        [i: string]: string;
    };
    runtime_options?: string[];
    aot_profiler_options?: AOTProfilerOptions;
    coverage_profiler_options?: CoverageProfilerOptions;
    ignore_pdb_load_errors?: boolean;
};
declare type MonoConfigError = {
    isError: true;
    message: string;
    error: any;
};
declare type AllAssetEntryTypes = AssetEntry | AssemblyEntry | SatelliteAssemblyEntry | VfsEntry | IcuData;
declare type AssetEntry = {
    name: string;
    behavior: AssetBehaviours;
    virtual_path?: string;
    culture?: string;
    load_remote?: boolean;
    is_optional?: boolean;
};
interface AssemblyEntry extends AssetEntry {
    name: "assembly";
}
interface SatelliteAssemblyEntry extends AssetEntry {
    name: "resource";
    culture: string;
}
interface VfsEntry extends AssetEntry {
    name: "vfs";
    virtual_path: string;
}
interface IcuData extends AssetEntry {
    name: "icu";
    load_remote: boolean;
}
declare const enum AssetBehaviours {
    Resource = "resource",
    Assembly = "assembly",
    Heap = "heap",
    ICU = "icu",
    VFS = "vfs"
}
declare const enum GlobalizationMode {
    ICU = "icu",
    INVARIANT = "invariant",
    AUTO = "auto"
}
declare type AOTProfilerOptions = {
    write_at?: string;
    send_to?: string;
};
declare type CoverageProfilerOptions = {
    write_at?: string;
    send_to?: string;
};
declare type EmscriptenModuleConfig = {
    disableDotNet6Compatibility?: boolean;
    config?: MonoConfig | MonoConfigError;
    configSrc?: string;
    onConfigLoaded?: () => void;
    onDotNetReady?: () => void;
    /**
     * @deprecated DEPRECATED! backward compatibility https://github.com/search?q=mono_bind_static_method&type=Code
     */
    mono_bind_static_method: (fqn: string, signature: string) => Function;
};

/**
 * Allocates a block of memory that can safely contain pointers into the managed heap.
 * The result object has get(index) and set(index, value) methods that can be used to retrieve and store managed pointers.
 * Once you are done using the root buffer, you must call its release() method.
 * For small numbers of roots, it is preferable to use the mono_wasm_new_root and mono_wasm_new_roots APIs instead.
 */
declare function mono_wasm_new_root_buffer(capacity: number, name?: string): WasmRootBuffer;
/**
 * Allocates temporary storage for a pointer into the managed heap.
 * Pointers stored here will be visible to the GC, ensuring that the object they point to aren't moved or collected.
 * If you already have a managed pointer you can pass it as an argument to initialize the temporary storage.
 * The result object has get() and set(value) methods, along with a .value property.
 * When you are done using the root you must call its .release() method.
 */
declare function mono_wasm_new_root<T extends ManagedPointer | NativePointer>(value?: T | undefined): WasmRoot<T>;
/**
 * Releases 1 or more root or root buffer objects.
 * Multiple objects may be passed on the argument list.
 * 'undefined' may be passed as an argument so it is safe to call this method from finally blocks
 *  even if you are not sure all of your roots have been created yet.
 * @param {... WasmRoot} roots
 */
declare function mono_wasm_release_roots(...args: WasmRoot<any>[]): void;
declare class WasmRootBuffer {
    private __count;
    private length;
    private __offset;
    private __offset32;
    private __ownsAllocation;
    constructor(offset: VoidPtr$1, capacity: number, ownsAllocation: boolean, name?: string);
    _throw_index_out_of_range(): void;
    _check_in_range(index: number): void;
    get_address(index: number): NativePointer;
    get_address_32(index: number): number;
    get(index: number): ManagedPointer;
    set(index: number, value: ManagedPointer): ManagedPointer;
    _unsafe_get(index: number): number;
    _unsafe_set(index: number, value: ManagedPointer | NativePointer): void;
    clear(): void;
    release(): void;
    toString(): string;
}
declare class WasmRoot<T extends ManagedPointer | NativePointer> {
    private __buffer;
    private __index;
    constructor(buffer: WasmRootBuffer, index: number);
    get_address(): NativePointer;
    get_address_32(): number;
    get(): T;
    set(value: T): T;
    get value(): T;
    set value(value: T);
    valueOf(): T;
    clear(): void;
    release(): void;
    toString(): string;
}

declare function mono_wasm_runtime_ready(): void;

declare function mono_wasm_setenv(name: string, value: string): void;
declare function mono_load_runtime_and_bcl_args(args: MonoConfig): Promise<void>;
declare function mono_wasm_load_data_archive(data: Uint8Array, prefix: string): boolean;
/**
 * Loads the mono config file (typically called mono-config.json) asynchroniously
 * Note: the run dependencies are so emsdk actually awaits it in order.
 *
 * @param {string} configFilePath - relative path to the config file
 * @throws Will throw an error if the config file loading fails
 */
declare function mono_wasm_load_config(configFilePath: string): Promise<void>;

declare function mono_wasm_load_icu_data(offset: VoidPtr$1): boolean;

declare function conv_string(mono_obj: MonoString): string | null;
declare function js_string_to_mono_string(string: string): MonoString | null;

declare function js_to_mono_obj(js_obj: any): MonoObject;
declare function js_typed_array_to_array(js_obj: any): MonoArray;

declare function unbox_mono_obj(mono_obj: MonoObject): any;
declare function mono_array_to_js_array(mono_array: MonoArray): any[] | null;

declare function mono_bind_static_method(fqn: string, signature: ArgsMarshalString): Function;
declare function mono_call_assembly_entry_point(assembly: string, args: any[], signature: ArgsMarshalString): any;

declare function mono_wasm_load_bytes_into_heap(bytes: Uint8Array): VoidPtr$1;

declare const MONO: MONO;
interface MONO {
    mono_wasm_runtime_ready: typeof mono_wasm_runtime_ready;
    mono_wasm_setenv: typeof mono_wasm_setenv;
    mono_wasm_load_data_archive: typeof mono_wasm_load_data_archive;
    mono_wasm_load_bytes_into_heap: typeof mono_wasm_load_bytes_into_heap;
    mono_wasm_load_icu_data: typeof mono_wasm_load_icu_data;
    mono_wasm_load_config: typeof mono_wasm_load_config;
    mono_load_runtime_and_bcl_args: typeof mono_load_runtime_and_bcl_args;
    mono_wasm_new_root_buffer: typeof mono_wasm_new_root_buffer;
    mono_wasm_new_root: typeof mono_wasm_new_root;
    mono_wasm_release_roots: typeof mono_wasm_release_roots;
    mono_wasm_add_assembly: (name: string, data: VoidPtr, size: number) => number;
    mono_wasm_load_runtime: (unused: string, debug_level: number) => void;
    loaded_files: string[];
    config: MonoConfig | MonoConfigError;
}
declare const BINDING: BINDING;
interface BINDING {
    mono_obj_array_new: (size: number) => MonoArray;
    mono_obj_array_set: (array: MonoArray, idx: number, obj: MonoObject) => void;
    js_string_to_mono_string: typeof js_string_to_mono_string;
    js_typed_array_to_array: typeof js_typed_array_to_array;
    js_to_mono_obj: typeof js_to_mono_obj;
    mono_array_to_js_array: typeof mono_array_to_js_array;
    conv_string: typeof conv_string;
    bind_static_method: typeof mono_bind_static_method;
    call_assembly_entry_point: typeof mono_call_assembly_entry_point;
    unbox_mono_obj: typeof unbox_mono_obj;
}
interface DotNetPublicAPI {
    MONO: MONO;
    BINDING: BINDING;
    Module: any;
}

declare function createDotnetRuntime(moduleFactory: (api: DotNetPublicAPI) => EmscriptenModuleConfig): Promise<DotNetPublicAPI>;

export { createDotnetRuntime as default };
