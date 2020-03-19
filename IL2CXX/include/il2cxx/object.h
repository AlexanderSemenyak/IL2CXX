#ifndef IL2CXX__OBJECT_H
#define IL2CXX__OBJECT_H

#include "heap.h"
#include "slot.h"

namespace il2cxx
{

struct t__extension;
struct t__weak_handle;
template<typename T>
struct t__type_of;

class t_object
{
	template<typename T, typename T_wait> friend class t_heap;
	friend class t_slot;
	friend struct t__type;
	friend struct t__type_finalizee;
	friend class t_thread;
	friend class t_engine;
	friend struct t__weak_handle;

	enum t_color : char
	{
		e_color__BLACK,
		e_color__PURPLE,
		e_color__GRAY,
		e_color__WHITING,
		e_color__WHITE,
		e_color__ORANGE,
		e_color__RED
	};

	static IL2CXX__PORTABLE__THREAD struct
	{
		t_object* v_next;
		t_object* v_previous;
	} v_roots;
	static IL2CXX__PORTABLE__THREAD t_object* v_scan_stack;
	static IL2CXX__PORTABLE__THREAD t_object* v_cycle;
	static IL2CXX__PORTABLE__THREAD t_object* v_cycles;

	IL2CXX__PORTABLE__FORCE_INLINE static void f_append(t_object* a_p)
	{
		a_p->v_next = reinterpret_cast<t_object*>(&v_roots);
		a_p->v_previous = v_roots.v_previous;
		a_p->v_previous->v_next = v_roots.v_previous = a_p;
	}
	static void f_push(t_object* a_p)
	{
		a_p->v_scan = v_scan_stack;
		v_scan_stack = a_p;
	}
	template<void (t_object::*A_push)()>
	static void f_push(t_slot& a_slot)
	{
		if (auto p = a_slot.v_p.load(std::memory_order_relaxed)) (p->*A_push)();
	}
	template<void (t_object::*A_push)()>
	static void f_push_and_clear(t_slot& a_slot)
	{
		auto p = a_slot.v_p.load(std::memory_order_relaxed);
		if (!p) return;
		(p->*A_push)();
		a_slot.v_p.store(nullptr, std::memory_order_relaxed);
	}
	static void f_collect();

	t_object* v_next;
	t_object* v_previous;
	t_object* v_scan;
	t_color v_color;
	bool v_finalizee = false;
	size_t v_count = 1;
	size_t v_cyclic;
	size_t v_rank;
	t_object* v_next_cycle;
	std::atomic<t__type*> v_type{nullptr};
	std::atomic<t__extension*> v_extension{nullptr};

	template<void (t_object::*A_push)()>
	void f_step();
	template<void (t_object::*A_step)()>
	void f_loop()
	{
		auto p = this;
		while (true) {
			(p->*A_step)();
			p = v_scan_stack;
			if (!p) break;
			v_scan_stack = p->v_scan;
		}
	}
	IL2CXX__PORTABLE__FORCE_INLINE void f_increment()
	{
		++v_count;
		v_color = e_color__BLACK;
	}
	void f_decrement_push()
	{
		if (--v_count > 0) {
			v_color = e_color__PURPLE;
			if (!v_next) f_append(this);
		} else {
			f_push(this);
		}
	}
	void f_decrement_step();
	void f_decrement();
	void f_mark_gray_push()
	{
		if (v_color != e_color__GRAY) {
			v_color = e_color__GRAY;
			v_cyclic = v_count;
			f_push(this);
		}
		--v_cyclic;
	}
	void f_mark_gray()
	{
		v_color = e_color__GRAY;
		v_cyclic = v_count;
		f_loop<&t_object::f_step<&t_object::f_mark_gray_push>>();
	}
	void f_scan_black_push()
	{
		if (v_color == e_color__BLACK) return;
		v_color = e_color__BLACK;
		f_push(this);
	}
	void f_scan_gray_scan_black_push()
	{
		if (v_color == e_color__BLACK) return;
		if (v_color != e_color__WHITING) f_push(this);
		v_color = e_color__BLACK;
	}
	void f_scan_gray_push()
	{
		if (v_color != e_color__GRAY) return;
		v_color = v_cyclic > 0 ? e_color__BLACK : e_color__WHITING;
		f_push(this);
	}
	void f_scan_gray_step()
	{
		if (v_color == e_color__BLACK) {
			f_step<&t_object::f_scan_gray_scan_black_push>();
		} else {
			v_color = e_color__WHITE;
			f_step<&t_object::f_scan_gray_push>();
		}
	}
	void f_scan_gray()
	{
		if (v_color != e_color__GRAY) return;
		if (v_cyclic > 0) {
			v_color = e_color__BLACK;
			f_loop<&t_object::f_step<&t_object::f_scan_black_push>>();
		} else {
			f_loop<&t_object::f_scan_gray_step>();
		}
	}
	void f_collect_white_push()
	{
		if (v_color != e_color__WHITE) return;
		v_color = e_color__ORANGE;
		v_next = v_cycle->v_next;
		v_cycle->v_next = this;
		f_push(this);
	}
	void f_collect_white()
	{
		v_color = e_color__ORANGE;
		v_cycle = v_next = this;
		f_loop<&t_object::f_step<&t_object::f_collect_white_push>>();
	}
	void f_scan_red()
	{
		if (v_color == e_color__RED && v_cyclic > 0) --v_cyclic;
	}
	void f_cyclic_decrement_push()
	{
		if (v_color == e_color__RED) return;
		if (v_color == e_color__ORANGE) {
			--v_count;
			--v_cyclic;
		} else {
			f_decrement();
		}
	}
	void f_cyclic_decrement();

public:
	template<typename T, typename T_construct>
	static T* f_new(size_t a_extra, T_construct a_construct);

	t__type* f_type() const
	{
		return v_type.load(std::memory_order_relaxed);
	}
	t__extension* f_extension();
	void f__scan(t_scan a_scan)
	{
	}
	void f__construct(t_object* a_p) const
	{
	}
	t_object* f__clone() const
	{
		return f_new<t_object>(0, [](auto)
		{
		});
	}
};

struct t__extension
{
	std::recursive_timed_mutex v_mutex;
	std::condition_variable_any v_condition;
	struct
	{
		t__weak_handle* v_previous;
		t__weak_handle* v_next;
	} v_weak_handles;
	t_slot v_weak_handles__cycle{};
	std::mutex v_weak_handles__mutex;

	t__extension();
	~t__extension();
	void f_detach();
	void f_scan(t_scan a_scan);
};

struct t__handle
{
	virtual ~t__handle() = default;
	virtual t_object* f_target() const = 0;
};

struct t__normal_handle : t__handle
{
	t_root<t_slot> v_target;

	t__normal_handle(t_object* a_target) : v_target(a_target)
	{
	}
	virtual t_object* f_target() const;
};

struct t__weak_handle : t__handle, decltype(t__extension::v_weak_handles)
{
	t_object* v_target;
	bool v_final;

	void f_attach(t_root<t_slot>& a_target);
	t_object* f_detach();

	t__weak_handle(t_object* a_target, bool a_final);
	virtual ~t__weak_handle();
	virtual t_object* f_target() const;
	virtual void f_target__(t_object* a_p);
	virtual void f_scan(t_scan a_scan);
};

inline t__extension::t__extension() : v_weak_handles{static_cast<t__weak_handle*>(&v_weak_handles), static_cast<t__weak_handle*>(&v_weak_handles)}
{
}

struct t__dependent_handle : t__weak_handle
{
	t_slot v_secondary;

	t__dependent_handle(t_object* a_target, t_object* a_secondary) : t__weak_handle(a_target, false), v_secondary(a_secondary)
	{
	}
	virtual ~t__dependent_handle();
	virtual void f_target__(t_object* a_p);
	virtual void f_scan(t_scan a_scan);
};

}

#endif
