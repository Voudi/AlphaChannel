using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

public sealed class HookManager : IDisposable
{
	[CompilerGenerated]
	private IGameInteropProvider _003C_provider_003EP;

	private readonly ConcurrentDictionary<string, (IDalamudHook, long)> _hooks;

	private Task? _currentTask;

	private bool _disposed;

	public IEnumerable<(string Name, nint Address, long Time, Type Delegate)> Diagnostics
	{
		get
		{
			if (!_disposed)
			{
				return _hooks.Select<KeyValuePair<string, (IDalamudHook, long)>, (string, nint, long, Type)>((KeyValuePair<string, (IDalamudHook, long)> kvp) => (kvp.Key, kvp.Value.Item1.Address, kvp.Value.Item2, kvp.Value.Item1.GetType().GenericTypeArguments[0]));
			}

			return Array.Empty<(string, nint, long, Type)>();
		}
	}

	public HookManager(IGameInteropProvider _provider)
	{
		_003C_provider_003EP = _provider;
		_hooks = new ConcurrentDictionary<string, (IDalamudHook, long)>();
	}

	public Task<Hook<T>> CreateHook<T>(string name, nint address, T detour, bool enable = false) where T : Delegate
	{
		T detour2 = detour;
		string name2 = name;
		CheckDisposed();
		if (address <= 0)
		{
			throw new Exception($"Creating Hook {name2} failed: address 0x{address:X} is invalid.");
		}

		return AppendTask(Func);
		Hook<T> Func()
		{
			Stopwatch timer = Stopwatch.StartNew();
			Hook<T> hook = _003C_provider_003EP.HookFromAddress(address, detour2);
			if (enable)
			{
				hook.Enable();
			}

			AddHook(name2, hook, timer);
			return hook;
		}
	}

	public Task<Hook<T>> CreateHook<T>(string name, string signature, T detour, bool enable = false) where T : Delegate
	{
		string signature2 = signature;
		T detour2 = detour;
		string name2 = name;
		CheckDisposed();
		return AppendTask(Func);
		Hook<T> Func()
		{
			Stopwatch timer = Stopwatch.StartNew();
			Hook<T> hook = _003C_provider_003EP.HookFromSignature(signature2, detour2);
			if (enable)
			{
				hook.Enable();
			}

			AddHook(name2, hook, timer);
			return hook;
		}
	}

	public Task<Hook<T>?> TryReplaceHook<T>(string name, T detour) where T : Delegate
	{
		string name2 = name;
		T detour2 = detour;
		CheckDisposed();
		return AppendTask(Func);
		Hook<T>? Func()
		{
			Stopwatch timer = Stopwatch.StartNew();
			if (!_hooks.TryRemove(name2, out (IDalamudHook, long) value))
			{
				return null;
			}

			bool isEnabled = value.Item1.IsEnabled;
			value.Item1.Dispose();
			Hook<T> hook = _003C_provider_003EP.HookFromAddress(value.Item1.Address, detour2);
			if (isEnabled)
			{
				hook.Enable();
			}

			AddHook(name2, hook, timer);
			return hook;
		}
	}

	public Task<bool> DisposeHook(string name)
	{
		string name2 = name;
		CheckDisposed();
		return AppendTask(Func);
		bool Func()
		{
			if (!_hooks.TryRemove(name2, out (IDalamudHook, long) value))
			{
				return false;
			}

			value.Item1.Dispose();
			return true;
		}
	}

	private Task<T> AppendTask<T>(Func<T> func)
	{
		Func<T> func2 = func;
		lock (_hooks)
		{
			return (Task<T>)(_currentTask = ((_currentTask == null || _currentTask.IsCompleted) ? Task.Run(func2) : _currentTask.ContinueWith((Task _) => func2(), TaskScheduler.Default)));
		}
	}

	private void CheckDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException("HookManager");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		lock (_hooks)
		{
			_currentTask?.Wait();
			_disposed = true;
			foreach (KeyValuePair<string, (IDalamudHook, long)> hook in _hooks)
			{
				hook.Deconstruct(out var _, out var value);
				value.Item1.Dispose();
			}

			_hooks.Clear();
			_currentTask = null;
		}
	}

	private void AddHook(string name, IDalamudHook hook, Stopwatch timer)
	{
		if (!_hooks.TryAdd(name, (hook, timer.ElapsedMilliseconds)))
		{
			throw new Exception("A hook with the name of " + name + " already exists.");
		}
	}
}