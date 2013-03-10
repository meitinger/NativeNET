Native .NET
===========


Description
-----------
This is a small helper program that allows managed libraries to be used in
unmanaged programs by directly exporting methods (i.e. without facilitating COM
or any other kind of interop/injection). The major difference between existing
tools (and the motivation behind the program) is that the assembly containing
the methods to be exported is not modified at all. Instead a small stub DLL is
created that exports the methods and forwards the calls to the managed library.
This allows methods from multiple assemblies to be exported into one DLL, but
it also ensures that you don't have to worry about your library getting
properly reassembled after disassembly and tagging methods with an `.export`
flag.


Quick Example
-------------
Imagine if you will, that you need to extend some Windows API, e.g. the fax
service provider API. This required you to export methods under certain names.
Here is an example of one of those methods, `FaxDevStartJob`:
(http://msdn.microsoft.com/en-us/library/windows/desktop/ms684541.aspx)
	
	public class YourManagedFaxDeviceImplementation
	{
		...

		[DllExport(Name="FaxDevStartJob")]
		public static bool NativeStartJob(IntPtr LineHandle, uint DeviceId, out IntPtr FaxHandle, IntPtr CompletionPortHandle, UIntPtr CompletionKey)
		{
			// managed implementation
			...
		}

		...
	}

The `DllExportAttribute` is defined in *Native .NET* which needs to be added to
the your project's references. Once your project is built, you would then run
the following command: (to automate this process, refer to the last section)

	NativeNET.exe /OUT=YourAssembly.Native.dll YourAssembly.dll

This generates `YourAssembly.Native.dll`, a managed DLL that exports any (see
the section below for a list of restrictions) method with the
`DllExportAttribute` in the usual (unmanaged) way. This is also the file you'd
then use in `FaxServer.RegisterDeviceProvider`. Of course, since it is just a
stub, you still have to put `YourAssembly.dll` either in the same directory,
or better yet: into the `GAC`.


About `DllExportAttribute`
--------------------------
This attribute is the key to exporting methods. But only methods that fulfill
the following requirements are even checked for this attribute:

* `static`
* non-generic (neither the method itself nor its class)
* `public`
* no *varargs* (`__arglist`)

The first two restrictions are fairly obvious, since they also apply to .NETs
`DllImportAttribute`. (Unmanaged code has no notion of what's `this` or what a
generic type is.)
The third one was a design choice and the last one isn't applicable since all
methods are exported to unmanaged code as `CallingConvention.StdCall`, which
makes it impossible to clean up an unknown amount of parameters from stack.

`DllExportAttribute` supports two properties:

* `Name`: This specifies the symbol (name) under which the method is to be
          exported. If it's missing, the method's full name will be used
          instead.
* `Ordinal`: The id at which the method is exported. If it's missing, a free
             ordinal (i.e. not assigned anywhere else) will be used.


About `DllMarshalAsAttribute`
-----------------------------
This attribute replaces .NETs `MarshalAsAttribute` and behaves in the same way.
It's necessary since `MarshalAsAttribute` is not persisted like any other
custom attribute and therefore cannot be properly reflected (using .NET's
reflection API). For further information have a look at:
http://msdn.microsoft.com/en-us/library/system.runtime.interopservices.marshalasattribute.aspx


Usage
-----
*Native .NET* has a fairly straightforward  syntax. Every argument not prefixed
with a `/` or `-` is regarded as an input assembly. At least one input assembly
is required. Only two options are recognized:

* `/OUT[PUT]=<path>`: This is the file name of the resulting
                      unmanaged-to-managed DLL.
* `/REF[ERENCE]=<path>`: A file name of a referenced assembly. This is
                         necessary if an input assembly's reference is not in
                         the same directory. (One `/REF` option per file name.)

Every other parameter is forwarded to `ilasm`. So if your stub DLL needs to
target (be called from) *x86-64* you'd simply specify `/X64`. This also means
that options are recognized only by their first three characters.


Caveat emptor
-------------
One point that should be mentioned is that before .NET 4 it was actually quite
dangerous to use managed in unmanaged code, because .NET 2 and below don't
support different Framework versions within the same process. That's why
Microsoft has even blocked managed assemblies from being registered as iFilters
in Windows 7.
The policy of *Native .NET* concerning the Framework version of the output
assembly is using the highest version of any input assembly. It is strongly
recommended to use .NET 4 or above in any assembly that will be exposed to
unmanaged code.
In addition, the highest used Framework version must be listed in `App.config`
as `supportedRuntime`. If this is not the case, the program cannot reflect on
any changes (e.g. new classes) introduced to `mscorlib` and will most likely
malfunction.


Integrating into `msbuild` (project files)
------------------------------------------
Usually you want your build process to be smarter than just calling a simple
after-built command. In addition you may have multiple projects in a solution
file that need to be made available to unmanaged code. To facilitate this, add
the following either to a certain project file or to a common target file:

	<PropertyGroup>
		<!-- adjust the '.Native.dll' part of the following line to your needs or leave it as-is -->
		<IntermediateNativeAssembly>$(IntermediateOutputPath)$(TargetName).Native.dll</IntermediateNativeAssembly>
		
		<!-- create a NativePlatform property, which basically is just $(Platform) but with a fallback to x86 -->
		<NativePlatform Condition="'$(NativePlatform)' == ''">$(Platform)</NativePlatform>
		<NativePlatform Condition="'$(NativePlatform)' == 'AnyCPU'">x86</NativePlatform>
	</PropertyGroup>

	<!-- the following target will pass all assembly references to NativeNET and will only run if NativeNET is required, i.e. if it is referenced by the current project -->
	<Target Name="Native" Inputs="@(IntermediateAssembly);@(ReferencePath)" Outputs="$(IntermediateNativeAssembly)" DependsOnTargets="ResolveAssemblyReferences" BeforeTargets="CopyFilesToOutputDirectory">
		<Exec Outputs="$(IntermediateNativeAssembly)" Condition="'%(ProjectReference.Project)' == '{0F0BFCFA-A9B9-4888-B501-15A55F49102F}'" Command="@(ProjectReference->'&quot;%(RootDir)%(Directory)bin\$(Configuration)\%(FileName).exe&quot;') /$(NativePlatform) &quot;/OUT=$(IntermediateNativeAssembly)&quot; @(IntermediateAssembly->'&quot;%(FullPath)&quot;', ' ') @(ReferencePath->'&quot;/REF=%(FullPath)&quot;', ' ')">
			<Output TaskParameter="Outputs" ItemName="AddModules"/>
		</Exec>
	</Target>
