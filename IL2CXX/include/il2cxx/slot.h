#ifndef IL2CXX__SLOT_H
#define IL2CXX__SLOT_H

#include "define.h"
#include <cassert>
#include <cinttypes>
#include <cstddef>
#include <condition_variable>
#include <atomic>
#include <mutex>
#include <type_traits>

namespace il2cxx
{

struct t__type;
class t_engine;
class t_object;
class t_thread;
t_engine* f_engine();

class t_slot
{
	friend class t_engine;
	friend class t_object;
	friend class t_thread;
	friend t_engine* f_engine();

public:
	class t_collector
	{
	protected:
		bool v_collector__running = true;
		bool v_collector__quitting = false;
		std::mutex v_collector__mutex;
		std::condition_variable v_collector__wake;
		std::condition_variable v_collector__done;
		size_t v_collector__tick = 0;
		size_t v_collector__wait = 0;
		size_t v_collector__epoch = 0;
		size_t v_collector__collect = 0;

		t_collector()
		{
			v_collector = this;
		}
		~t_collector()
		{
			v_collector = nullptr;
		}

	public:
		void f_tick()
		{
			if (v_collector__running) return;
			std::lock_guard<std::mutex> lock(v_collector__mutex);
			++v_collector__tick;
			if (v_collector__running) return;
			v_collector__running = true;
			v_collector__wake.notify_one();
		}
		void f_wait();
	};

protected:
	template<size_t A_SIZE>
	struct t_queue
	{
		static const size_t V_SIZE = A_SIZE;

		t_object* volatile* v_head{v_objects};
		t_object* volatile* v_next = v_objects + V_SIZE / 2;
		t_object* volatile v_objects[V_SIZE];
		t_object* volatile* v_epoch;
		t_object* volatile* v_tail{v_objects + V_SIZE - 1};

		void f_next() noexcept;
		IL2CXX__PORTABLE__ALWAYS_INLINE IL2CXX__PORTABLE__FORCE_INLINE void f_push(t_object* a_object)
		{
			*v_head = a_object;
			if (v_head == v_next)
				f_next();
			else
				++v_head;
		}
		template<typename T>
		void f__flush(t_object* volatile* a_epoch, T a_do)
		{
			auto end = v_objects + V_SIZE - 1;
			if (a_epoch > v_objects)
				--a_epoch;
			else
				a_epoch = end;
			while (v_tail != a_epoch) {
				auto next = a_epoch;
				if (v_tail < end) {
					if (next < v_tail) next = end;
					++v_tail;
				} else {
					v_tail = v_objects;
				}
				while (true) {
					a_do(*v_tail);
					if (v_tail == next) break;
					++v_tail;
				}
			}
		}
	};
#ifdef NDEBUG
	struct t_increments : t_queue<16384>
#else
	struct t_increments : t_queue<128>
#endif
	{
		void f_flush()
		{
			f__flush(v_epoch, [](auto x)
			{
				x->f_increment();
			});
		}
	};
#ifdef NDEBUG
	struct t_decrements : t_queue<32768>
#else
	struct t_decrements : t_queue<256>
#endif
	{
		t_object* volatile* v_last = v_objects;

		void f_flush()
		{
			f__flush(v_last, [](auto x)
			{
				x->f_decrement();
			});
			v_last = v_epoch;
		}
	};
	class t_pass
	{
	};

	static IL2CXX__PORTABLE__THREAD t_collector* v_collector;
	static IL2CXX__PORTABLE__THREAD t_increments* v_increments;
	static IL2CXX__PORTABLE__THREAD t_decrements* v_decrements;

#ifdef IL2CXX__PORTABLE__SUPPORTS_THREAD_EXPORT
	static t_increments* f_increments()
	{
		return v_increments;
	}
	static t_decrements* f_decrements()
	{
		return v_decrements;
	}
#else
	static IL2CXX__PORTABLE__EXPORT t_increments* f_increments();
	static IL2CXX__PORTABLE__EXPORT t_decrements* f_decrements();
#endif

	std::atomic<t_object*> v_p;

	t_slot(t_object* a_p, const t_pass&) : v_p(a_p)
	{
	}
	void f_assign(t_object* a_p)
	{
		if (a_p) f_increments()->f_push(a_p);
		if (auto p = v_p.exchange(a_p)) f_decrements()->f_push(p);
	}
	void f_assign(t_object& a_p)
	{
		f_increments()->f_push(&a_p);
		if (auto p = v_p.exchange(&a_p)) f_decrements()->f_push(p);
	}
	IL2CXX__PORTABLE__ALWAYS_INLINE void f_assign(const t_slot& a_value)
	{
		auto p = a_value.v_p.load();
		if (p) f_increments()->f_push(p);
		p = v_p.exchange(p);
		if (p) f_decrements()->f_push(p);
	}
	IL2CXX__PORTABLE__ALWAYS_INLINE void f_assign(t_slot&& a_value)
	{
		if (&a_value == this) return;
		auto p = v_p.exchange(a_value.v_p.exchange(nullptr));
		if (p) f_decrements()->f_push(p);
	}

public:
	t_slot(t_object* a_p = nullptr) : v_p(a_p)
	{
		if (v_p) f_increments()->f_push(v_p);
	}
	t_slot(const t_slot& a_value) : v_p(a_value.v_p.load())
	{
		if (auto p = v_p.load()) f_increments()->f_push(p);
	}
	t_slot(t_slot&& a_value) : v_p(a_value.v_p.exchange(nullptr))
	{
	}
	t_slot& operator=(const t_slot& a_value)
	{
		f_assign(a_value);
		return *this;
	}
	t_slot& operator=(t_slot&& a_value)
	{
		f_assign(std::move(a_value));
		return *this;
	}
	bool operator==(const t_slot& a_value) const
	{
		return v_p == a_value.v_p;
	}
	bool operator!=(const t_slot& a_value) const
	{
		return !operator==(a_value);
	}
	operator bool() const
	{
		return v_p;
	}
	operator t_object*() const
	{
		return v_p;
	}
	template<typename T>
	explicit operator T*() const
	{
		return static_cast<T*>(v_p.load());
	}
	t_object* operator->() const
	{
		return v_p;
	}
	void f_construct(const t_slot& a_value)
	{
		assert(!v_p);
		auto p = a_value.v_p.load();
		if (p) f_increments()->f_push(p);
		v_p = p;
	}
	void f_construct(t_slot&& a_value)
	{
		f_construct(a_value);
		a_value.f__destruct();
	}
	IL2CXX__PORTABLE__ALWAYS_INLINE void f__destruct()
	{
		if (!v_p) return;
		f_decrements()->f_push(v_p);
		v_p = nullptr;
	}
};

template<typename T>
class t_slot_of : public t_slot
{
	friend struct t__type;
	friend class t_object;

	t_slot_of(T* a_p, const t_pass&) : t_slot(a_p, t_pass())
	{
	}

public:
	t_slot_of(T* a_p = nullptr) : t_slot(a_p)
	{
	}
	t_slot_of(const t_slot& a_value) : t_slot(a_value)
	{
	}
	t_slot_of(t_slot&& a_value) : t_slot(std::move(a_value))
	{
	}
	t_slot_of(const t_slot_of& a_value) : t_slot(a_value)
	{
	}
	t_slot_of(t_slot_of&& a_value) : t_slot(std::move(a_value))
	{
	}
	t_slot_of& operator=(T* a_p)
	{
		f_assign(a_p);
		return *this;
	}
	IL2CXX__PORTABLE__ALWAYS_INLINE t_slot_of& operator=(const t_slot& a_value)
	{
		f_assign(a_value);
		return *this;
	}
	IL2CXX__PORTABLE__ALWAYS_INLINE t_slot_of& operator=(t_slot&& a_value)
	{
		f_assign(std::move(a_value));
		return *this;
	}
	t_slot_of& operator=(const t_slot_of& a_value)
	{
		f_assign(a_value);
		return *this;
	}
	t_slot_of& operator=(t_slot_of&& a_value)
	{
		f_assign(std::move(a_value));
		return *this;
	}
	/*void f_construct(T* a_p = nullptr)
	{
		assert(!v_p);
		if (a_p) f_increments()->f_push(a_p);
		v_p = a_p;
	}*/
	operator T*() const
	{
		return static_cast<T*>(v_p.load());
	}
	T* operator->() const
	{
		return static_cast<T*>(v_p.load());
	}
};

template<typename T>
struct t_scoped : T
{
	using T::T;
	template<typename U>
	t_scoped(U&& a_value) : T(std::forward<U>(a_value))
	{
	}
	template<typename U>
	t_scoped(const t_scoped<U>& a_value) : T(a_value)
	{
	}
	template<typename U>
	t_scoped(t_scoped<U>&& a_value) : T(std::move(a_value))
	{
	}
	~t_scoped()
	{
		this->f__destruct();
	}
	template<typename U>
	t_scoped& operator=(U&& a_value)
	{
		static_cast<T&>(*this) = std::forward<U>(a_value);
		return *this;
	}
};

template<size_t A_SIZE>
void t_slot::t_queue<A_SIZE>::f_next() noexcept
{
	v_collector->f_tick();
	if (v_head < v_objects + V_SIZE - 1) {
		++v_head;
		while (v_tail == v_head) v_collector->f_wait();
		auto tail = v_tail;
		v_next = std::min(tail < v_head ? v_objects + V_SIZE - 1 : tail - 1, v_head + V_SIZE / 2);
	} else {
		v_head = v_objects;
		while (v_tail == v_head) v_collector->f_wait();
		v_next = std::min(v_tail - 1, v_head + V_SIZE / 2);
	}
}

typedef void (*t_scan)(t_slot&);

}

#endif