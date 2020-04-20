#ifndef IL2CXX__TYPE_H
#define IL2CXX__TYPE_H

#include "object.h"

namespace il2cxx
{

struct t__member_info : t_object
{
};

struct t__type : t__member_info
{
	t__type* v__base;
	std::map<t__type*, std::pair<void**, void**>> v__interface_to_methods;
	bool v__managed;
	size_t v__size;
	t__type* v__element;
	size_t v__rank;
	void* v__multicast_invoke;

	t__type(t__type* a_base, std::map<t__type*, std::pair<void**, void**>>&& a_interface_to_methods, bool a_managed, size_t a_size, t__type* a_element = nullptr, size_t a_rank = 0, void* a_multicast_invoke = nullptr);
	IL2CXX__PORTABLE__ALWAYS_INLINE void f__finish(t_object* a_p)
	{
		//t_slot::f_increments()->f_push(this);
		a_p->v_type.store(this, std::memory_order_release);
		t_slot::f_decrements()->f_push(a_p);
	}
	virtual void f_scan(t_object* a_this, t_scan a_scan);
	virtual t_object* f_clone(const t_object* a_this);
	virtual void f_register_finalize(t_object* a_this);
	virtual void f_suppress_finalize(t_object* a_this);
	virtual void f_copy(const char* a_from, size_t a_n, char* a_to);
	bool f__is(t__type* a_type) const
	{
		auto p = this;
		do {
			if (p == a_type) return true;
			p = p->v__base;
		} while (p);
		return false;
	}
	void** f__implementation(t__type* a_interface) const
	{
		auto i = v__interface_to_methods.find(a_interface);
		return i == v__interface_to_methods.end() ? nullptr : i->second.first;
	}
};

struct t__type_finalizee : t__type
{
	using t__type::t__type;
	IL2CXX__PORTABLE__ALWAYS_INLINE void f__finish(t_object* a_p)
	{
		a_p->v_finalizee = true;
		t__type::f__finish(a_p);
	}
	virtual void f_register_finalize(t_object* a_this);
	virtual void f_suppress_finalize(t_object* a_this);
};

template<typename T_interface, size_t A_i>
void* f__resolve(t_object* a_this)
{
	return a_this->f_type()->v__interface_to_methods[&t__type_of<T_interface>::v__instance].second[A_i];
}

template<typename T_interface, size_t A_i, typename T_r, typename... T_an>
T_r f__invoke(t_object* a_this, T_an... a_n, void** a_site)
{
	auto p = a_this->f_type()->v__interface_to_methods[&t__type_of<T_interface>::v__instance].first[A_i];
	*a_site = p;
	return reinterpret_cast<T_r(*)(t_object*, T_an..., void**)>(p)(a_this, a_n..., a_site);
}

template<typename T_interface, size_t A_i, typename T_type, typename T_method, T_method A_method, typename T_r, typename... T_an>
T_r f__method(t_object* a_this, T_an... a_n, void** a_site)
{
	return a_this->f_type() == &t__type_of<T_type>::v__instance ? A_method(static_cast<T_type*>(a_this), a_n...) : f__invoke<T_interface, A_i, T_r, T_an...>(a_this, a_n..., a_site);
}

template<typename T_interface, size_t A_i, size_t A_j>
void* f__generic_resolve(t_object* a_this)
{
	return reinterpret_cast<void**>(a_this->f_type()->v__interface_to_methods[&t__type_of<T_interface>::v__instance].second[A_i])[A_j];
}

template<typename T_interface, size_t A_i, size_t A_j, typename T_r, typename... T_an>
T_r f__generic_invoke(t_object* a_this, T_an... a_n, void** a_site)
{
	auto p = reinterpret_cast<void**>(a_this->f_type()->v__interface_to_methods[&t__type_of<T_interface>::v__instance].first[A_i])[A_j];
	*a_site = p;
	return reinterpret_cast<T_r(*)(t_object*, T_an..., void**)>(p)(a_this, a_n..., a_site);
}

template<typename T_interface, size_t A_i, size_t A_j, typename T_type, typename T_method, T_method A_method, typename T_r, typename... T_an>
T_r f__generic_method(t_object* a_this, T_an... a_n, void** a_site)
{
	return a_this->f_type() == &t__type_of<T_type>::v__instance ? A_method(static_cast<T_type*>(a_this), a_n...) : f__generic_invoke<T_interface, A_i, A_j, T_r, T_an...>(a_this, a_n..., a_site);
}

}

#endif
